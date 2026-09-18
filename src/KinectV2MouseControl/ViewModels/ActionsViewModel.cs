using System;
using System.Collections.Generic;

namespace KinectV2MouseControl
{
    /// <summary>
    /// One catalog entry with a Run command, for the Actions page.
    /// </summary>
    public class ActionItemViewModel
    {
        public ActionDescriptor Descriptor { get; private set; }
        public RelayCommand RunCommand { get; private set; }

        public ActionItemViewModel(ActionDescriptor descriptor, Action<ActionDescriptor> run)
        {
            Descriptor = descriptor;
            RunCommand = new RelayCommand(() => run(descriptor), () => descriptor.IsImplemented && descriptor.CanRunFromUi);
        }

        public string Name { get { return Descriptor.Name; } }
        public string Description { get { return Descriptor.Description; } }
        public string TriggerText { get { return Descriptor.TriggerText; } }
        public string StatusText { get { return Descriptor.StatusText; } }
        public bool IsImplemented { get { return Descriptor.IsImplemented; } }
        public bool CanRun { get { return Descriptor.IsImplemented && Descriptor.CanRunFromUi; } }
    }

    public class ActionGroupViewModel
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public List<ActionItemViewModel> Items { get; set; }
        public int AvailableCount { get; set; }
        public int PlannedCount { get; set; }

        public string CountText
        {
            get
            {
                return PlannedCount == 0
                    ? AvailableCount + " available"
                    : AvailableCount + " available · " + PlannedCount + " planned";
            }
        }
    }

    /// <summary>
    /// The Actions page: the catalog grouped by category, each entry runnable through the same
    /// router the gestures use. This is the page that makes Input → Intent → Action visible.
    /// </summary>
    public class ActionsViewModel
    {
        private readonly KinectCursorViewModel engine;
        private readonly Action<string> shellCommand;

        public List<ActionGroupViewModel> Groups { get; private set; }

        public int AvailableCount { get; private set; }
        public int PlannedCount { get; private set; }

        public ActionsViewModel(KinectCursorViewModel engine, Action<string> shellCommand)
        {
            this.engine = engine;
            this.shellCommand = shellCommand;

            Groups = new List<ActionGroupViewModel>();
            foreach (ActionCategory category in Enum.GetValues(typeof(ActionCategory)))
            {
                ActionGroupViewModel group = new ActionGroupViewModel();
                group.Title = ActionCatalog.DescribeCategory(category);
                group.Detail = ActionCatalog.DescribeCategoryDetail(category);
                group.Items = new List<ActionItemViewModel>();

                for (int i = 0; i < ActionCatalog.All.Length; i++)
                {
                    ActionDescriptor descriptor = ActionCatalog.All[i];
                    if (descriptor.Category != category)
                    {
                        continue;
                    }

                    group.Items.Add(new ActionItemViewModel(descriptor, Run));
                    if (descriptor.IsImplemented)
                    {
                        group.AvailableCount++;
                        AvailableCount++;
                    }
                    else
                    {
                        group.PlannedCount++;
                        PlannedCount++;
                    }
                }

                if (group.Items.Count > 0)
                {
                    Groups.Add(group);
                }
            }
        }

        private void Run(ActionDescriptor descriptor)
        {
            if (!descriptor.IsImplemented)
            {
                return;
            }

            if (!string.IsNullOrEmpty(descriptor.ShellCommand))
            {
                shellCommand(descriptor.ShellCommand);
                return;
            }

            engine.ExecuteAction(descriptor.Action, "control center");
        }
    }
}
