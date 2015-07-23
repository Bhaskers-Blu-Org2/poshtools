﻿using System;
using System.Diagnostics;
using System.ServiceModel;
using log4net;
using PowerShellTools.Common;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;
using PowerShellTools.DebugEngine;
using PowerShellTools.Options;

namespace PowerShellTools.ServiceManagement
{
    /// <summary>
    /// Manage the process and channel creation.
    /// </summary>
    internal sealed class ConnectionManager
    {
        private IPowerShellIntelliSenseService _powerShellIntelliSenseService;
        private IPowerShellDebuggingService _powerShellDebuggingService;
        private object _syncObject = new object();
        private static object _staticSyncObject = new object();
        private static ConnectionManager _instance;
        private Process _process;
        private ChannelFactory<IPowerShellIntelliSenseService> _intelliSenseServiceChannelFactory;
        private ChannelFactory<IPowerShellDebuggingService> _debuggingServiceChannelFactory;
        private static readonly ILog Log = LogManager.GetLogger(typeof(PowerShellToolsPackage));
        private PowerShellHostProcess _hostProcess;

        /// <summary>
        /// Event is fired when the connection exception happened.
        /// </summary>
        public event EventHandler ConnectionException;

        private ConnectionManager()
        {
            OpenClientConnection();
        }

        /// <summary>
        /// Connection manager instance.
        /// </summary>
        public static ConnectionManager Instance
        {
            get
            {
                lock (_staticSyncObject)
                {
                    if (_instance == null)
                    {
                        _instance = new ConnectionManager();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// The IntelliSense service channel.
        /// </summary>
        public IPowerShellIntelliSenseService PowerShellIntelliSenseSerivce
        {
            get
            {
                if (_powerShellIntelliSenseService == null)
                {
                    OpenClientConnection();
                }

                return _powerShellIntelliSenseService;
            }
        }

        /// <summary>
        /// The debugging service channel.
        /// </summary>
        public IPowerShellDebuggingService PowerShellDebuggingService
        {
            get
            {
                if (_powerShellDebuggingService == null)
                {
                    OpenClientConnection();
                }
                return _powerShellDebuggingService;
            }
        }

        /// <summary>
        /// PowerShell host process
        /// </summary>
        public PowerShellHostProcess HostProcess
        {
            get
            {
                return _hostProcess;
            }
        }

        private void OpenClientConnection()
        {
            lock (_syncObject)
            {
                if (_powerShellIntelliSenseService == null || _powerShellDebuggingService == null)
                {
                    EnsureCloseProcess();
                    var page = PowerShellToolsPackage.Instance.GetDialogPage<GeneralDialogPage>();
                    _hostProcess = PowershellHostProcessHelper.CreatePowerShellHostProcess(page.Bitness);
                    _process = _hostProcess.Process;
                    _process.Exited += ConnectionExceptionHandler;

                    // net.pipe://localhost/UniqueEndpointGuid/{RelativeUri}
                    var intelliSenseServiceEndPointAddress = Constants.ProcessManagerHostUri + _hostProcess.EndpointGuid + "/" + Constants.IntelliSenseHostRelativeUri;
                    var deubggingServiceEndPointAddress = Constants.ProcessManagerHostUri + _hostProcess.EndpointGuid + "/" + Constants.DebuggingHostRelativeUri;

                    try
                    {
                        _intelliSenseServiceChannelFactory = ChannelFactoryHelper.CreateDuplexChannelFactory<IPowerShellIntelliSenseService>(intelliSenseServiceEndPointAddress, new InstanceContext(PowerShellToolsPackage.Instance.IntelliSenseServiceContext));
                        _intelliSenseServiceChannelFactory.Faulted += ConnectionExceptionHandler;
                        _intelliSenseServiceChannelFactory.Closed += ConnectionExceptionHandler;
                        _intelliSenseServiceChannelFactory.Open();
                        _powerShellIntelliSenseService = _intelliSenseServiceChannelFactory.CreateChannel();

                        _debuggingServiceChannelFactory = ChannelFactoryHelper.CreateDuplexChannelFactory<IPowerShellDebuggingService>(deubggingServiceEndPointAddress, new InstanceContext(new DebugServiceEventsHandlerProxy()));
                        _debuggingServiceChannelFactory.Faulted += ConnectionExceptionHandler;
                        _debuggingServiceChannelFactory.Closed += ConnectionExceptionHandler;
                        _debuggingServiceChannelFactory.Open();
                        _powerShellDebuggingService = _debuggingServiceChannelFactory.CreateChannel();
                        _powerShellDebuggingService.SetRunspace(PowerShellToolsPackage.OverrideExecutionPolicyConfiguration);
                    }
                    catch (Exception e)
                    {
                        // Connection has to be established...
                        Log.Error(string.Format("Connection establish failed... Error message: {0}", e.ToString()));
                        EnsureCloseProcess();

                        _powerShellIntelliSenseService = null;
                        _powerShellDebuggingService = null;
                        throw;
                    }
                }
            }
        }

        public void ProcessEventHandler(BitnessOptions bitness)
        {
            Log.DebugFormat("Bitness had been changed to {1}", bitness);
            EnsureCloseProcess();
        }

        private void EnsureCloseProcess()
        {
            if (_process != null)
            {
                try
                {
                    _process.Kill();
                    _process = null;
                }
                catch (Exception ex)
                {
                    Log.ErrorFormat("Error when closing process.  Message: {0}", ex.Message);
                }
            }
        }

        private void ConnectionExceptionHandler(object sender, EventArgs e)
        {
            PowerShellToolsPackage.DebuggerReadyEvent.Reset();

            EnsureClearServiceChannel();

            if (ConnectionException != null)
            {
                ConnectionException(this, EventArgs.Empty);
            }
        }

        private void EnsureClearServiceChannel()
        {
            if (_intelliSenseServiceChannelFactory != null)
            {
                _intelliSenseServiceChannelFactory.Abort();
                _intelliSenseServiceChannelFactory = null;
                _powerShellIntelliSenseService = null;
            }

            if (_debuggingServiceChannelFactory != null)
            {
                _debuggingServiceChannelFactory.Abort();
                _debuggingServiceChannelFactory = null;
                _powerShellDebuggingService = null;
            }

        }
    }
}
