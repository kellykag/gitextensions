﻿using GitCommands;
using GitExtUtils;
using GitUI.HelperDialogs;
using GitUI.NBugReports;
using GitUIPluginInterfaces;
using ResourceManager;

namespace GitUI.ScriptsEngine
{
    partial class ScriptsManager
    {
        /// <summary>Runs scripts.</summary>
        internal static class ScriptRunner
        {
            private const string PluginPrefix = "plugin:";
            private const string NavigateToPrefix = "navigateTo:";

            public static CommandStatus RunScript<THostForm>(ScriptInfo script, THostForm form, RevisionGridControl? revisionGrid)
                where THostForm : IGitModuleForm, IWin32Window
            {
                try
                {
                    return RunScriptInternal(script, form, form.UICommands, revisionGrid);
                }
                catch (ExternalOperationException ex) when (ex is not UserExternalOperationException)
                {
                    throw new UserExternalOperationException($"{TranslatedStrings.ScriptErrorFailedToExecute}: '{script.Name}'", ex);
                }
            }

            private static CommandStatus RunScriptInternal(ScriptInfo script, IWin32Window owner, IGitUICommands uiCommands, RevisionGridControl? revisionGrid)
            {
                if (string.IsNullOrEmpty(script.Command))
                {
                    return false;
                }

                string? arguments = script.Arguments;
                if (!string.IsNullOrEmpty(arguments) && revisionGrid is null)
                {
                    string? optionDependingOnSelectedRevision
                        = ScriptOptionsParser.Options.FirstOrDefault(option => ScriptOptionsParser.DependsOnSelectedRevision(option)
                                                                            && ScriptOptionsParser.Contains(arguments, option));
                    if (optionDependingOnSelectedRevision is not null)
                    {
                        throw new UserExternalOperationException($"{TranslatedStrings.ScriptText}: '{script.Name}'{Environment.NewLine}'{optionDependingOnSelectedRevision}' {TranslatedStrings.ScriptErrorOptionWithoutRevisionGridText}",
                            new ExternalOperationException(script.Command, arguments, uiCommands.GitModule.WorkingDir));
                    }
                }

                if (script.AskConfirmation &&
                    MessageBox.Show(owner, $"{TranslatedStrings.ScriptConfirmExecute}: '{script.Name}'?", TranslatedStrings.ScriptText,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    return false;
                }

                string? originalCommand = script.Command;
                (string? argument, bool abort) = ScriptOptionsParser.Parse(script.Arguments, uiCommands, owner, revisionGrid);
                if (abort)
                {
                    throw new UserExternalOperationException($"{TranslatedStrings.ScriptText}: '{script.Name}'{Environment.NewLine}{TranslatedStrings.ScriptErrorOptionWithoutRevisionText}",
                        new ExternalOperationException(script.Command, arguments, uiCommands.GitModule.WorkingDir));
                }

                string command = OverrideCommandWhenNecessary(originalCommand);
                command = ExpandCommandVariables(command, uiCommands.GitModule);

                if (script.IsPowerShell)
                {
                    PowerShellHelper.RunPowerShell(command, argument, uiCommands.GitModule.WorkingDir, script.RunInBackground);

                    // 'RunPowerShell' always runs the script detached (yet).
                    // Hence currently, it does not make sense to set 'needsGridRefresh' to '!scriptInfo.RunInBackground'.
                    return new CommandStatus(executed: true, needsGridRefresh: false);
                }

                if (command.StartsWith(PluginPrefix))
                {
                    command = command.Replace(PluginPrefix, string.Empty);

                    lock (PluginRegistry.Plugins)
                    {
                        foreach (var plugin in PluginRegistry.Plugins)
                        {
                            if (string.Equals(plugin.Name, command, StringComparison.CurrentCultureIgnoreCase))
                            {
                                GitUIEventArgs eventArgs = new(owner, uiCommands);
                                return new CommandStatus(executed: true, needsGridRefresh: plugin.Execute(eventArgs));
                            }
                        }
                    }

                    return false;
                }

                if (command.StartsWith(NavigateToPrefix))
                {
                    if (revisionGrid is null)
                    {
                        return false;
                    }

                    command = command.Replace(NavigateToPrefix, string.Empty);
                    if (!string.IsNullOrEmpty(command))
                    {
                        ExecutionResult result = new Executable(command, uiCommands.GitModule.WorkingDir).Execute(argument);
                        string revisionRef = result.StandardOutput.Split('\n').FirstOrDefault();

                        if (revisionRef is not null)
                        {
                            revisionGrid.GoToRef(revisionRef, true);
                        }
                    }

                    return new CommandStatus(executed: true, needsGridRefresh: false);
                }

                if (!script.RunInBackground)
                {
                    bool success = FormProcess.ShowDialog(owner, commands: null, argument, uiCommands.GitModule.WorkingDir, null, true, process: command);
                    if (!success)
                    {
                        return false;
                    }
                }
                else
                {
                    if (originalCommand.Equals("{openurl}", StringComparison.CurrentCultureIgnoreCase))
                    {
                        OsShellUtil.OpenUrlInDefaultBrowser(argument);
                    }
                    else
                    {
                        // It is totally valid to have a command without an argument, e.g.:
                        //    Command  : myscript.cmd
                        //    Arguments: <blank>
                        new Executable(command, uiCommands.GitModule.WorkingDir).Start(argument ?? string.Empty);
                    }
                }

                return new CommandStatus(executed: true, needsGridRefresh: !script.RunInBackground);
            }

            private static string ExpandCommandVariables(string originalCommand, IGitModule module)
            {
                return originalCommand.Replace("{WorkingDir}", module.WorkingDir);
            }

            private static string OverrideCommandWhenNecessary(string originalCommand)
            {
                // Make sure we are able to run git, even if git is not in the path
                if (originalCommand.Equals("git", StringComparison.CurrentCultureIgnoreCase) ||
                    originalCommand.Equals("{git}", StringComparison.CurrentCultureIgnoreCase))
                {
                    return AppSettings.GitCommand;
                }

                if (originalCommand.Equals("gitextensions", StringComparison.CurrentCultureIgnoreCase) ||
                    originalCommand.Equals("{gitextensions}", StringComparison.CurrentCultureIgnoreCase) ||
                    originalCommand.Equals("gitex", StringComparison.CurrentCultureIgnoreCase) ||
                    originalCommand.Equals("{gitex}", StringComparison.CurrentCultureIgnoreCase))
                {
                    return AppSettings.GetGitExtensionsFullPath();
                }

                if (originalCommand.Equals("{openurl}", StringComparison.CurrentCultureIgnoreCase))
                {
                    return "explorer";
                }

                // Prefix should be {plugin:pluginname},{plugin=pluginname}
                var match = System.Text.RegularExpressions.Regex.Match(originalCommand, @"\{plugin.(.+)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    originalCommand = $"{PluginPrefix}{match.Groups[1].Value.ToLower()}";
                }

                return originalCommand;
            }
        }
    }
}
