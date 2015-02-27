﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using log4net;
using System.Collections.ObjectModel;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using DTE = EnvDTE;
using DTE80 = EnvDTE80;
using PowerShellTools.Common.Debugging;

namespace PowerShellTools.DebugEngine
{
    public class EventArgs<T> : EventArgs
    {
        public EventArgs(T value)
        {
            Value = value;
        }

        public T Value { get; private set; }
    }

    /// <summary>
    /// This is the main debugger for PowerShell Tools for Visual Studio
    /// </summary>
    public partial class ScriptDebugger
    {
        private List<ScriptBreakpoint> _breakpoints;
        private List<ScriptStackFrame> _callstack;

        /// <summary>
        /// Event is fired when a breakpoint is hit.
        /// </summary>
        public event EventHandler<EventArgs<ScriptBreakpoint>> BreakpointHit;

        /// <summary>
        /// Event is fired when a debugger is paused.
        /// </summary>
        public event EventHandler<EventArgs<ScriptLocation>> DebuggerPaused;

        /// <summary>
        /// Event is fired when a breakpoint is updated.
        /// </summary>
        public event EventHandler<DebuggerBreakpointUpdatedEventArgs> BreakpointUpdated;

        /// <summary>
        /// Event is fired when a string is output from the PowerShell host.
        /// </summary>
        public event EventHandler<EventArgs<string>> OutputString;

        /// <summary>
        /// Event is fired when the debugger has finished.
        /// </summary>
        public event EventHandler DebuggingFinished;

        /// <summary>
        /// Event is fired when a terminating exception is thrown.
        /// </summary>
        public event EventHandler<EventArgs<Exception>> TerminatingException;

        private readonly AutoResetEvent _pausedEvent = new AutoResetEvent(false);

        private string _debuggingCommand;

        /// <summary>
        /// The current set of variables for the current runspace.
        /// </summary>
        public IDictionary<string, Variable> Variables { get; private set; }

        /// <summary>
        /// The current call stack for the runspace.
        /// </summary>
        public IEnumerable<ScriptStackFrame> CallStack { get { return _callstack; } }

        /// <summary>
        /// The currently executing <see cref="ScriptProgramNode"/>
        /// </summary>
        public ScriptProgramNode CurrentExecutingNode { get; private set; }

        /// <summary>
        /// Indicate if debugger is ready for accepting command
        /// </summary>
        public bool DebuggingCommandReady { get; private set; }

        /// <summary>
        /// Indicate if runspace is hosting remote session
        /// </summary>
        public bool RemoteSession { get; set; }

        private static readonly ILog Log = LogManager.GetLogger(typeof(ScriptDebugger));

        /// <summary>
        /// Sets breakpoints for the current runspace.
        /// </summary>
        /// <remarks>
        /// This method clears any existing breakpoints.
        /// </remarks>
        /// <param name="initialBreakpoints"></param>
        public void SetBreakpoints(IEnumerable<ScriptBreakpoint> initialBreakpoints)
        {
            _breakpoints = new List<ScriptBreakpoint>();

            if (initialBreakpoints == null) return;

            Log.InfoFormat("ScriptDebugger: Initial Breakpoints: {0}", initialBreakpoints.Count());
            ClearBreakpoints();

            foreach (var bp in initialBreakpoints)
            {
                SetBreakpoint(bp);
                _breakpoints.Add(bp);
                bp.Bind();
            }
        }

        /// <summary>
        /// Clears existing breakpoints for the current runspace.
        /// </summary>
        private void ClearBreakpoints()
        {
            try
            {
                DebuggingService.ClearBreakpoints();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to clear existing breakpoints", ex);
            }
        }

        private void SetBreakpoint(ScriptBreakpoint breakpoint)
        {
            Log.InfoFormat("SetBreakpoint: {0} {1} {2}", breakpoint.File, breakpoint.Line, breakpoint.Column);

            try
            {
                DebuggingService.SetBreakpoint(new PowershellBreakpoint(breakpoint.File, breakpoint.Line, breakpoint.Column));
            }
            catch (Exception ex)
            {
                Log.Error("Failed to set breakpoint.", ex);
            }
        }
        
        #region Debugging service event handlers

        /// <summary>
        /// Debugger stopped handler
        /// </summary>
        /// <param name="e"></param>
        public void DebuggerStop(DebuggerStoppedEventArgs e)
        {
            Log.InfoFormat("Debugger stopped");

            RefreshScopedVariables();
            RefreshCallStack();

            if (!ProcessLineBreakpoints(e.ScriptFullPath, e.Line, e.Column))
            {
                if (DebuggerPaused != null)
                {
                    var scriptLocation = new ScriptLocation(e.ScriptFullPath, e.Line, 0);

                    DebuggerPaused(this, new EventArgs<ScriptLocation>(scriptLocation));
                }
            }

            Log.Debug("Waiting for debuggee to resume.");

            DebuggingCommandReady = true;

            //Wait for the user to step, continue or stop
            _pausedEvent.WaitOne();
            Log.DebugFormat("Debuggee resume action is {0}", _debuggingCommand);

            DebuggingService.ExecuteDebuggingCommand(_debuggingCommand);

            DebuggingCommandReady = false;
        }

        /// <summary>
        /// PS execution terminating excpetion handler
        /// </summary>
        /// <param name="ex"></param>
        public void TerminateException(DebuggingServiceException ex)
        {
            if (TerminatingException != null)
            {
                TerminatingException(this, new EventArgs<Exception>(new Exception(ex.Message, new Exception(ex.InnerExceptionMessage))));
            }
        }

        /// <summary>
        /// Breakpoint has been updated
        /// </summary>
        /// <param name="e"></param>
        public void UpdateBreakpoint(DebuggerBreakpointUpdatedEventArgs e)
        {
            Log.InfoFormat("Breakpoint updated: {0} {1}", e.UpdateType, e.Breakpoint);

            if (BreakpointUpdated != null)
            {
                BreakpointUpdated(this, e);
            }
        }

        /// <summary>
        /// PSDebugger event finished handler
        /// </summary>
        public void DebuggerFinished()
        {
            DebuggingCommandReady = false;
            if (DebuggingFinished != null)
            {
                DebuggingFinished(this, new EventArgs());
            }
        }

        private void ConnectionExceptionHandler(object sender, EventArgs e)
        {
            Log.Error("Connection to host service is broken, terminating debugging.");
            DebuggerFinished();
        }

        #endregion

        /// <summary>
        /// Retrieve local scoped variable from debugger(in PSHost proc)
        /// </summary>
        private void RefreshScopedVariables()
        {
            try
            {
                Collection<Variable> vars = DebuggingService.GetScopedVariable();
                Variables = new Dictionary<string, Variable>();
                foreach (Variable v in vars)
                {
                    Variables.Add(v.VarName, v);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to refresh scoped variables.", ex);
            }
        }

        /// <summary>
        /// Retrieve callstack info from debugger(in PSHost proc)
        /// </summary>
        private void RefreshCallStack()
        {
            IEnumerable<CallStack> result = null;
            try
            {
                result = DebuggingService.GetCallStack();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to refresh callstack", ex);
            }

            _callstack = new List<ScriptStackFrame>();
            if (result == null) return;

            foreach (var psobj in result)
            {
                _callstack.Add(new ScriptStackFrame(CurrentExecutingNode, psobj.ScriptFullPath, psobj.Line, psobj.FrameString));
            }
        }

        private bool ProcessLineBreakpoints(string script, int line, int column)
        {
            Log.InfoFormat("Process Line Breapoints");

            var bp =
                _breakpoints.FirstOrDefault(
                    m =>
                    m.Column == column && line == m.Line &&
                    script.Equals(m.File, StringComparison.InvariantCultureIgnoreCase));

            if (bp != null)
            {
                if (BreakpointHit != null)
                {
                    Log.InfoFormat("Breakpoint @ {0} {1} {2} was hit.", bp.File, bp.Line, bp.Column);
                    BreakpointHit(this, new EventArgs<ScriptBreakpoint>(bp));
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Stops execution of the current script.
        /// </summary>
        public void Stop()
        {
            Log.Info("Stop");

            try
            {
                if (DebuggingCommandReady)
                {
                    _debuggingCommand = PowerShellConstants.Debugger_Stop;
                    _pausedEvent.Set();
                }
                else
                {
                    DebuggingService.Stop();
                }
            }
            catch (Exception ex)
            {
                //BUGBUG: Suppressing an exception that is thrown when stopping...
                Log.Debug("Error while stopping script...", ex);
            }
            finally
            {
                DebuggerFinished();
            }
        }

        /// <summary>
        /// Stop over block. 
        /// </summary>
        public void StepOver()
        {
            Log.Info("StepOver");
            _debuggingCommand = PowerShellConstants.Debugger_StepOver;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Step into block.
        /// </summary>
        public void StepInto()
        {
            Log.Info("StepInto");
            _debuggingCommand = PowerShellConstants.Debugger_StepInto;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Step out of block.
        /// </summary>
        public void StepOut()
        {
            Log.Info("StepOut");
            _debuggingCommand = PowerShellConstants.Debugger_StepOut;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Continue execution.
        /// </summary>
        public void Continue()
        {
            Log.Info("Continue");
            _debuggingCommand = PowerShellConstants.Debugger_Continue;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Execute the specified command line.
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        public bool Execute(string commandLine)
        {
             Log.Info("Execute");

            try
            {
                return ExecuteInternal(commandLine);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to execute script", ex);
                OutputString(this, new EventArgs<string>(ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Execute the specified command line 
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        public bool ExecuteInternal(string commandLine)
        {
            DebuggingCommandReady = false;
            return DebuggingService.Execute(commandLine);
        }

        /// <summary>
        /// Execute the specified command line as debugging command.
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        public void ExecuteDebuggingCommand(string commandLine)
        {
            Log.Info("Execute debugging command");

            if (DebuggingCommandReady)
            {
                try
                {
                    DebuggingService.ExecuteDebuggingCommand(commandLine);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to execute debugging command", ex);
                }
            }
        }

        /// <summary>
        /// Execute the current program node.
        /// </summary>
        /// <remarks>
        /// The node will either be a script file or script content; depending on the node 
        /// passed to this function.
        /// </remarks>
        /// <param name="node"></param>
        public void Execute(ScriptProgramNode node)
        {
            CurrentExecutingNode = node;

            if (node.IsAttachedProgram)
            {
                Execute(String.Format("Enter-PSHostProcess -Id {0};", node.Process.ProcessId));
                Execute("Debug-Runspace 1");
            }
            else
            {
                string commandLine = node.FileName;

                if (node.IsFile)
                {
                    commandLine = String.Format(DebugEngineConstants.ExecutionCommandFormat, node.FileName, node.Arguments);
                }
                Execute(commandLine);
            }
        }

        void objects_DataAdded(object sender, DataAddedEventArgs e)
        {
            if (OutputString != null)
            {
                var list = sender as PSDataCollection<PSObject>;
                OutputString(this, new EventArgs<string>(list[e.Index] + Environment.NewLine));
            }
        }

        public void SetVariable(string name, string value)
        {
            try
            {
                using (var pipeline = (_runspace.CreateNestedPipeline()))
                {
                    var command = new Command("Set-Variable");
                    command.Parameters.Add("Name", name);
                    command.Parameters.Add("Value", value);

                    pipeline.Commands.Add(command);
                    pipeline.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to set variable.", ex);
            }
        }

        public Variable GetVariable(string name)
        {
            if (name.StartsWith("$"))
            {
                name = name.Remove(0, 1);
            }

            if (Variables.ContainsKey(name))
            {
                var var = Variables[name];
                return var;
            }

            return null;
        }

        internal void OpenRemoteFile(string fullName)
        {
            var dte2 = (DTE80.DTE2)Package.GetGlobalService(typeof(DTE.DTE));

            if (dte2 != null)
            {
                try
                {
                    dte2.ItemOperations.OpenFile(fullName);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to open remote file through powershell remote session", ex);
                    OutputString(this, new EventArgs<string>(ex.Message));
                }
            }
        }
    }

    /// <summary>
    /// Location within a script.
    /// </summary>
    public class ScriptLocation
    {
        /// <summary>
        /// The full path to the file.
        /// </summary>
        public string File { get; set; }
        /// <summary>
        /// Line number within the file.
        /// </summary>
        public int Line { get; set; }
        /// <summary>
        /// Column within the file.
        /// </summary>
        public int Column { get; set; }

        public ScriptLocation(string file, int line, int column)
        {
            File = file;
            Line = line;
            Column = column;
        }
    }
}