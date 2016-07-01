﻿using System.IO;
using Infralution.Localization.Wpf;
using Microsoft.Vbe.Interop;
using NLog;
using Rubberduck.Common;
using Rubberduck.Common.Dispatch;
using Rubberduck.Parsing;
using Rubberduck.Parsing.Symbols;
using Rubberduck.Parsing.VBA;
using Rubberduck.Settings;
using Rubberduck.UI;
using Rubberduck.UI.Command.MenuItems;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rubberduck.UI.SourceControl;
using Rubberduck.VBEditor.Extensions;

namespace Rubberduck
{
    public sealed class App : IDisposable
    {
        private const string FILE_TARGET_NAME = "file";
        private readonly VBE _vbe;
        private readonly IMessageBox _messageBox;
        private IRubberduckParser _parser;
        private AutoSave.AutoSave _autoSave;
        private IGeneralConfigService _configService;
        private readonly IAppMenu _appMenus;
        private RubberduckCommandBar _stateBar;
        private IRubberduckHooks _hooks;
        private bool _handleSinkEvents = true;
        private readonly BranchesViewViewModel _branchesVM;
        private readonly SourceControlViewViewModel _sourceControlPanelVM;
        private readonly UI.Settings.Settings _settings;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private VBProjectsEventsSink _sink;
        private Configuration _config;

        private readonly IConnectionPoint _projectsEventsConnectionPoint;
        private readonly int _projectsEventsCookie;

        private readonly IDictionary<string, Tuple<IConnectionPoint, int>> _componentsEventsConnectionPoints =
            new Dictionary<string, Tuple<IConnectionPoint, int>>();
        private readonly IDictionary<string, Tuple<IConnectionPoint, int>> _referencesEventsConnectionPoints =
            new Dictionary<string, Tuple<IConnectionPoint, int>>();

        public App(VBE vbe, IMessageBox messageBox,
            UI.Settings.Settings settings,
            IRubberduckParser parser,
            IGeneralConfigService configService,
            IAppMenu appMenus,
            RubberduckCommandBar stateBar,
            IRubberduckHooks hooks,
            SourceControlDockablePresenter sourceControlPresenter)
        {
            _vbe = vbe;
            _messageBox = messageBox;
            _settings = settings;
            _parser = parser;
            _configService = configService;
            _autoSave = new AutoSave.AutoSave(_vbe, _configService);
            _appMenus = appMenus;
            _stateBar = stateBar;
            _hooks = hooks;

            var sourceControlPanel = (SourceControlPanel) sourceControlPresenter.Window();
            _sourceControlPanelVM = (SourceControlViewViewModel) sourceControlPanel.ViewModel;
            _branchesVM = (BranchesViewViewModel) _sourceControlPanelVM.TabItems.Single(t => t.ViewModel.Tab == SourceControlTab.Branches).ViewModel;

            _sourceControlPanelVM.OpenRepoStarted += DisableSinkEventHandlers;
            _sourceControlPanelVM.OpenRepoCompleted += EnableSinkEventHandlersAndUpdateCache;

            _branchesVM.LoadingComponentsStarted += DisableSinkEventHandlers;
            _branchesVM.LoadingComponentsCompleted += EnableSinkEventHandlersAndUpdateCache;

            _hooks.MessageReceived += _hooks_MessageReceived;
            _configService.SettingsChanged += _configService_SettingsChanged;
            _parser.State.StateChanged += Parser_StateChanged;
            _parser.State.StatusMessageUpdate += State_StatusMessageUpdate;
            _stateBar.Refresh += _stateBar_Refresh;

            _sink = new VBProjectsEventsSink();
            var connectionPointContainer = (IConnectionPointContainer)_vbe.VBProjects;
            var interfaceId = typeof(_dispVBProjectsEvents).GUID;
            connectionPointContainer.FindConnectionPoint(ref interfaceId, out _projectsEventsConnectionPoint);

            _sink.ProjectAdded += sink_ProjectAdded;
            _sink.ProjectRemoved += sink_ProjectRemoved;
            _sink.ProjectActivated += sink_ProjectActivated;
            _sink.ProjectRenamed += sink_ProjectRenamed;

            _projectsEventsConnectionPoint.Advise(_sink, out _projectsEventsCookie);
            UiDispatcher.Initialize();
        }

        private void EnableSinkEventHandlersAndUpdateCache(object sender, EventArgs e)
        {
            _handleSinkEvents = true;

            // update cache
            _parser.State.RemoveProject(_vbe.ActiveVBProject.HelpFile);
            _parser.State.AddProject(_vbe.ActiveVBProject);

            _parser.State.OnParseRequested(this);
        }

        private void DisableSinkEventHandlers(object sender, EventArgs e)
        {
            _handleSinkEvents = false;
        }

        private void State_StatusMessageUpdate(object sender, RubberduckStatusMessageEventArgs e)
        {
            var message = e.Message;
            if (message == ParserState.LoadingReference.ToString())
            {
                // note: ugly hack to enable Rubberduck.Parsing assembly to do this
                message = RubberduckUI.ParserState_LoadingReference;
            }

            _stateBar.SetStatusText(message);
        }

        private void _hooks_MessageReceived(object sender, HookEventArgs e)
        {
            RefreshSelection();
        }

        private ParserState _lastStatus;
        private Declaration _lastSelectedDeclaration;

        private void RefreshSelection()
        {
            var selectedDeclaration = _parser.State.FindSelectedDeclaration(_vbe.ActiveCodePane);
            _stateBar.SetSelectionText(selectedDeclaration);

            var currentStatus = _parser.State.Status;
            if (ShouldEvaluateCanExecute(selectedDeclaration, currentStatus))
            {
                _appMenus.EvaluateCanExecute(_parser.State);
            }

            _lastStatus = currentStatus;
            _lastSelectedDeclaration = selectedDeclaration;
        }

        private bool ShouldEvaluateCanExecute(Declaration selectedDeclaration, ParserState currentStatus)
        {
            return _lastStatus != currentStatus ||
                   (selectedDeclaration != null && !selectedDeclaration.Equals(_lastSelectedDeclaration)) ||
                   (selectedDeclaration == null && _lastSelectedDeclaration != null);
        }

        private void _configService_SettingsChanged(object sender, ConfigurationChangedEventArgs e)
        {
            _config = _configService.LoadConfiguration();
            _hooks.HookHotkeys();
            // also updates the ShortcutKey text
            _appMenus.Localize();
            UpdateLoggingLevel();

            if (e.LanguageChanged)
            {
                LoadConfig();
            }
        }

        private void EnsureDirectoriesExist()
        {
            try
            {
                if (!Directory.Exists(ApplicationConstants.LOG_FOLDER_PATH))
                {
                    Directory.CreateDirectory(ApplicationConstants.LOG_FOLDER_PATH);
                }
            }
            catch
            {
                //Does this need to display some sort of dialog?
            }
        }

        private void UpdateLoggingLevel()
        {
            LogLevelHelper.SetMinimumLogLevel(LogLevel.FromOrdinal(_config.UserSettings.GeneralSettings.MinimumLogLevel));
        }

        public void Startup()
        {
            EnsureDirectoriesExist();
            LoadConfig();
            _appMenus.Initialize();
            _hooks.HookHotkeys(); // need to hook hotkeys before we localize menus, to correctly display ShortcutTexts
            _appMenus.Localize();
            Task.Delay(1000).ContinueWith(t => UiDispatcher.Invoke(() => _parser.State.OnParseRequested(this)));
            UpdateLoggingLevel();
        }

        public void Shutdown()
        {
            try
            {
                _hooks.Detach();
            }
            catch
            {
                // Won't matter anymore since we're shutting everything down anyway.
            }
        }

        #region sink handlers. todo: move to another class
        async void sink_ProjectRemoved(object sender, DispatcherEventArgs<VBProject> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (e.Item.Protection == vbext_ProjectProtection.vbext_pp_locked)
            {
                Logger.Debug("Locked project '{0}' was removed.", e.Item.Name);
                return;
            }

            _parser.Cancel();

            var projectId = e.Item.HelpFile;
            Debug.Assert(projectId != null);

            _componentsEventsSinks.Remove(projectId);
            _referencesEventsSinks.Remove(projectId);
            _parser.State.RemoveProject(e.Item);
            _parser.State.OnParseRequested(this);

            Logger.Debug("Project '{0}' was removed.", e.Item.Name);
            Tuple<IConnectionPoint, int> componentsTuple;
            if (_componentsEventsConnectionPoints.TryGetValue(projectId, out componentsTuple))
            {
                componentsTuple.Item1.Unadvise(componentsTuple.Item2);
                _componentsEventsConnectionPoints.Remove(projectId);
            }

            Tuple<IConnectionPoint, int> referencesTuple;
            if (_referencesEventsConnectionPoints.TryGetValue(projectId, out referencesTuple))
            {
                referencesTuple.Item1.Unadvise(referencesTuple.Item2);
                _referencesEventsConnectionPoints.Remove(projectId);
            }
        }

        private readonly IDictionary<string, VBComponentsEventsSink> _componentsEventsSinks =
            new Dictionary<string, VBComponentsEventsSink>();

        private readonly IDictionary<string, ReferencesEventsSink> _referencesEventsSinks =
            new Dictionary<string, ReferencesEventsSink>();

        async void sink_ProjectAdded(object sender, DispatcherEventArgs<VBProject> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            Logger.Debug("Project '{0}' was added.", e.Item.Name);
            if (e.Item.Protection == vbext_ProjectProtection.vbext_pp_locked)
            {
                Logger.Debug("Project is protected and will not be added to parser state.");
                return;
            }

            _parser.State.AddProject(e.Item); // note side-effect: assigns ProjectId/HelpFile
            var projectId = e.Item.HelpFile;
            RegisterComponentsEventSink(e.Item.VBComponents, projectId);

            if (!_parser.State.AllDeclarations.Any())
            {
                // forces menus to evaluate their CanExecute state:
                Parser_StateChanged(this, new ParserStateEventArgs(ParserState.Pending));
                _stateBar.SetStatusText();
                return;
            }

            _parser.State.OnParseRequested(sender);
        }

        private void RegisterComponentsEventSink(VBComponents components, string projectId)
        {
            if (_componentsEventsSinks.ContainsKey(projectId))
            {
                // already registered - this is caused by the initial load+rename of a project in the VBE
                Logger.Debug("Components sink already registered.");
                return;
            }

            var connectionPointContainer = (IConnectionPointContainer)components;
            var interfaceId = typeof(_dispVBComponentsEvents).GUID;

            IConnectionPoint connectionPoint;
            connectionPointContainer.FindConnectionPoint(ref interfaceId, out connectionPoint);

            var componentsSink = new VBComponentsEventsSink();
            componentsSink.ComponentActivated += sink_ComponentActivated;
            componentsSink.ComponentAdded += sink_ComponentAdded;
            componentsSink.ComponentReloaded += sink_ComponentReloaded;
            componentsSink.ComponentRemoved += sink_ComponentRemoved;
            componentsSink.ComponentRenamed += sink_ComponentRenamed;
            componentsSink.ComponentSelected += sink_ComponentSelected;
            _componentsEventsSinks.Add(projectId, componentsSink);

            int cookie;
            connectionPoint.Advise(componentsSink, out cookie);

            _componentsEventsConnectionPoints.Add(projectId, Tuple.Create(connectionPoint, cookie));
            Logger.Debug("Components sink registered and advising.");
        }

        async void sink_ComponentSelected(object sender, DispatcherEventArgs<VBComponent> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }
            
            // todo: keep Code Explorer in sync with Project Explorer
        }

        async void sink_ComponentRenamed(object sender, DispatcherRenamedEventArgs<VBComponent> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }

            _parser.Cancel();

            _sourceControlPanelVM.HandleRenamedComponent(e.Item, e.OldName);

            Logger.Debug("Component '{0}' was renamed to '{1}'.", e.OldName, e.Item.Name);
            
            var projectId = e.Item.Collection.Parent.HelpFile;
            var componentDeclaration = _parser.State.AllDeclarations.FirstOrDefault(f =>
                        f.ProjectId == projectId &&
                        f.DeclarationType == DeclarationType.ClassModule &&
                        f.IdentifierName == e.OldName);

            if (e.Item.Type == vbext_ComponentType.vbext_ct_Document &&
                componentDeclaration != null &&

                // according to ThunderFrame, Excel is the only one we explicitly support
                // with two Document-component types just skip the Worksheet component
                ((ClassModuleDeclaration) componentDeclaration).Supertypes.All(a => a.IdentifierName != "Worksheet"))
            {
                _componentsEventsSinks.Remove(projectId);
                _referencesEventsSinks.Remove(projectId);
                _parser.State.RemoveProject(projectId);

                Logger.Debug("Project '{0}' was removed.", e.Item.Name);
                Tuple<IConnectionPoint, int> componentsTuple;
                if (_componentsEventsConnectionPoints.TryGetValue(projectId, out componentsTuple))
                {
                    componentsTuple.Item1.Unadvise(componentsTuple.Item2);
                    _componentsEventsConnectionPoints.Remove(projectId);
                }

                Tuple<IConnectionPoint, int> referencesTuple;
                if (_referencesEventsConnectionPoints.TryGetValue(projectId, out referencesTuple))
                {
                    referencesTuple.Item1.Unadvise(referencesTuple.Item2);
                    _referencesEventsConnectionPoints.Remove(projectId);
                }

                _parser.State.AddProject(e.Item.Collection.Parent);
            }
            else
            {
                _parser.State.RemoveRenamedComponent(e.Item, e.OldName);
            }

            _parser.State.OnParseRequested(this);
        }

        async void sink_ComponentRemoved(object sender, DispatcherEventArgs<VBComponent> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }

            _parser.Cancel(e.Item);

            _sourceControlPanelVM.HandleRemovedComponent(e.Item);

            Logger.Debug("Component '{0}' was removed.", e.Item.Name);
            _parser.State.ClearStateCache(e.Item, true);
        }

        async void sink_ComponentReloaded(object sender, DispatcherEventArgs<VBComponent> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }

            _parser.Cancel(e.Item);
            
            _parser.State.OnParseRequested(sender, e.Item);
        }

        async void sink_ComponentAdded(object sender, DispatcherEventArgs<VBComponent> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }

            _sourceControlPanelVM.HandleAddedComponent(e.Item);

            Logger.Debug("Component '{0}' was added.", e.Item.Name);
            _parser.State.OnParseRequested(sender, e.Item);
        }

        async void sink_ComponentActivated(object sender, DispatcherEventArgs<VBComponent> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }
            
            // do something?
        }

        async void sink_ProjectRenamed(object sender, DispatcherRenamedEventArgs<VBProject> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }

            _parser.Cancel();

            Logger.Debug("Project '{0}' (ID {1}) was renamed to '{2}'.", e.OldName, e.Item.HelpFile, e.Item.Name);

            _parser.State.RemoveProject(e.Item.HelpFile);
            _parser.State.AddProject(e.Item);

            _parser.State.OnParseRequested(sender);
        }

        async void sink_ProjectActivated(object sender, DispatcherEventArgs<VBProject> e)
        {
            if (!_handleSinkEvents || !_vbe.IsInDesignMode()) { return; }

            if (!_parser.State.AllDeclarations.Any())
            {
                return;
            }
            
            // todo: keep Code Explorer in sync with Project Explorer
        }
        #endregion

        private void _stateBar_Refresh(object sender, EventArgs e)
        {
            // handles "refresh" button click on "Rubberduck" command bar
            _parser.State.OnParseRequested(sender);
        }

        private void Parser_StateChanged(object sender, EventArgs e)
        {
            Logger.Debug("App handles StateChanged ({0}), evaluating menu states...", _parser.State.Status);
            _appMenus.EvaluateCanExecute(_parser.State);
        }

        private void LoadConfig()
        {
            _config = _configService.LoadConfiguration();

            _autoSave.ConfigServiceSettingsChanged(this, EventArgs.Empty);

            var currentCulture = RubberduckUI.Culture;
            try
            {
                CultureManager.UICulture = CultureInfo.GetCultureInfo(_config.UserSettings.GeneralSettings.Language.Code);
                _appMenus.Localize();
            }
            catch (CultureNotFoundException exception)
            {
                Logger.Error(exception, "Error Setting Culture for Rubberduck");
                _messageBox.Show(exception.Message, "Rubberduck", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _config.UserSettings.GeneralSettings.Language.Code = currentCulture.Name;
                _configService.SaveConfiguration(_config);
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_sourceControlPanelVM != null)
            {
                _sourceControlPanelVM.OpenRepoStarted -= DisableSinkEventHandlers;
                _sourceControlPanelVM.OpenRepoCompleted -= EnableSinkEventHandlersAndUpdateCache;
            }

            if (_branchesVM != null)
            {
                _branchesVM.LoadingComponentsStarted -= DisableSinkEventHandlers;
                _branchesVM.LoadingComponentsCompleted -= EnableSinkEventHandlersAndUpdateCache;
            }

            _handleSinkEvents = false;

            if (_parser != null && _parser.State != null)
            {
                _parser.State.StateChanged -= Parser_StateChanged;
                _parser.State.StatusMessageUpdate -= State_StatusMessageUpdate;
                _parser.Dispose();
                // I won't set this to null because other components may try to release things
            }

            if (_hooks != null)
            {
                _hooks.MessageReceived -= _hooks_MessageReceived;
                _hooks.Dispose();
                _hooks = null;
            }

            if (_settings != null)
            {
                _settings.Dispose();
            }

            if (_configService != null)
            {
                _configService.SettingsChanged -= _configService_SettingsChanged;
                _configService = null;
            }

            if (_stateBar != null)
            {
                _stateBar.Refresh -= _stateBar_Refresh;
                _stateBar.Dispose();
                _stateBar = null;
            }

            if (_sink != null)
            {
                _sink.ProjectAdded -= sink_ProjectAdded;
                _sink.ProjectRemoved -= sink_ProjectRemoved;
                _sink.ProjectActivated -= sink_ProjectActivated;
                _sink.ProjectRenamed -= sink_ProjectRenamed;
                _sink = null;
            }

            foreach (var item in _componentsEventsSinks)
            {
                item.Value.ComponentActivated -= sink_ComponentActivated;
                item.Value.ComponentAdded -= sink_ComponentAdded;
                item.Value.ComponentReloaded -= sink_ComponentReloaded;
                item.Value.ComponentRemoved -= sink_ComponentRemoved;
                item.Value.ComponentRenamed -= sink_ComponentRenamed;
                item.Value.ComponentSelected -= sink_ComponentSelected;
            }

            if (_autoSave != null)
            {
                _autoSave.Dispose();
                _autoSave = null;
            }

            _projectsEventsConnectionPoint.Unadvise(_projectsEventsCookie);
            foreach (var item in _componentsEventsConnectionPoints)
            {
                item.Value.Item1.Unadvise(item.Value.Item2);
            }
            foreach (var item in _referencesEventsConnectionPoints)
            {
                item.Value.Item1.Unadvise(item.Value.Item2);
            }

            UiDispatcher.Shutdown();

            _disposed = true;
        }
    }
}
