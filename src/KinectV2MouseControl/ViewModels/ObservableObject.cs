using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Minimal INotifyPropertyChanged base for the control-center view models. Set only raises
    /// when the value actually changed, which matters for the live status object: it is
    /// refreshed five times a second and most values are unchanged most of the time.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            Raise(propertyName);
            return true;
        }

        protected void Raise(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// ICommand over a delegate, so buttons in the XAML bind to view-model methods instead of
    /// routing through code-behind click handlers.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> execute;
        private readonly Func<object, bool> canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }

            this.execute = execute;
            this.canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(execute == null ? (Action<object>)null : p => execute(),
                   canExecute == null ? (Func<object, bool>)null : p => canExecute())
        {
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            return canExecute == null || canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            execute(parameter);
        }
    }
}
