﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PowerShellTools.DebugEngine.RunspacePickerUI
{
    class RunspacePickerWindowViewModel : INotifyPropertyChanged
    {
        private string _runspaceName;

        public string RunspaceName
        {
            get
            {
                return _runspaceName;
            }
            set
            {
                if (_runspaceName != value)
                {
                    _runspaceName = value;

                    OnPropertyChanged("RunspaceName");
                }
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            var evt = PropertyChanged;
            if (evt != null)
            {
                evt(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>
        /// Raised when the value of a property changes.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
