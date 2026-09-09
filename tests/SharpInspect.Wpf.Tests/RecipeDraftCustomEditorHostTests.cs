using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed partial class RecipeDraftEditorTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("algorithm")]
    [InlineData("schemaVersion")]
    [InlineData("schemaHash")]
    [InlineData("duplicate")]
    [InlineData("descriptorFailure")]
    [InlineData("overflow")]
    public Task V129_H01_MissingIncompatibleAmbiguousOrUnboundedRegistrationKeepsGenericAvailable(string scenario) =>
        RunCustomEditorSta(() =>
        {
            var descriptor = Descriptor(false);
            var editor = new FakeEditor(descriptor, Defaults(descriptor));
            var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
            model.AlgorithmExecutionTimeoutText = "250";
            model.RefreshAsync().GetAwaiter().GetResult();
            var factory = new CustomTestFactory(descriptor);
            var current = factory.Descriptor;
            if (scenario == "algorithm") factory.DescriptorValue = new(current.Editor,
                new AlgorithmIdentity(current.Algorithm.Id, "different"), current.Schema);
            if (scenario == "schemaVersion") factory.DescriptorValue = new(current.Editor, current.Algorithm,
                new RecipeContractReference(current.Schema.Id, "different", current.Schema.ContentHash));
            if (scenario == "schemaHash") factory.DescriptorValue = new(current.Editor, current.Algorithm,
                new RecipeContractReference(current.Schema.Id, current.Schema.Version, new string('C', 64)));
            factory.ThrowDescriptor = scenario == "descriptorFailure";
            var factories = scenario switch
            {
                "missing" => Array.Empty<IAlgorithmConfigurationEditorFactory>(),
                "duplicate" => new IAlgorithmConfigurationEditorFactory[] { factory, new CustomTestFactory(descriptor) },
                "overflow" => Enumerable.Repeat<IAlgorithmConfigurationEditorFactory>(factory, 65).ToArray(),
                _ => new IAlgorithmConfigurationEditorFactory[] { factory }
            };
            var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(factories));
            Assert.False(panel.TryUseCustomEditor());
            Assert.Equal(0, factory.CreateCount);
            Assert.Equal(Visibility.Visible, ((Border)panel.FindName("GenericEditorContainer")).Visibility);
            Assert.True(model.SaveAsync().GetAwaiter().GetResult()!.Saved);
            panel.ClearSensitiveInputs();
            model.DisposeAsync().GetAwaiter().GetResult();
        });

    [Fact]
    public Task V129_H02_RealPanelExchangesSameConfigurationWithoutInheritingHostDataContext() =>
        RunCustomEditorSta(() =>
        {
            var descriptor = Descriptor(false);
            var editor = new FakeEditor(descriptor, Defaults(descriptor));
            var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
            model.AlgorithmExecutionTimeoutText = "250";
            model.RefreshAsync().GetAwaiter().GetResult();
            var factory = new CustomTestFactory(descriptor);
            var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(new[] { factory }));
            Assert.True(panel.TryUseCustomEditor());
            var context = factory.Context!;
            Assert.NotSame(model, factory.Session!.View.DataContext);
            Assert.Equal(Visibility.Collapsed, ((Border)panel.FindName("GenericEditorContainer")).Visibility);
            factory.Session.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var acceptedHash = context.Read()!.Configuration!.ContentHash;
            Assert.Equal("31", Assert.Single(model.Fields, field => field.Key == "Count").InputText);
            panel.UseGenericEditor();
            Assert.Null(context.Read());
            Assert.Equal(Visibility.Visible, ((Border)panel.FindName("GenericEditorContainer")).Visibility);
            Assert.Equal(acceptedHash, model.ConfigurationContentHash);
            Assert.Single(model.Fields, field => field.Key == "Count").InputText = "32";
            Assert.True(panel.TryUseCustomEditor());
            Assert.Equal(32, factory.Context!.Read()!.Values.Single(value => value.Key == "Count").Value.AsInt64());
            Assert.Equal(model.ConfigurationContentHash, factory.Context.Read()!.Configuration!.ContentHash);
            panel.UseGenericEditor();
            model.DisposeAsync().GetAwaiter().GetResult();
        });

    [Theory]
    [InlineData("create")]
    [InlineData("nullView")]
    [InlineData("refresh")]
    [InlineData("secondRefresh")]
    [InlineData("reportedDuringCreate")]
    public Task V129_H03_InitializationFailureDiscardsNoDraftInputAndCannotWriteBeforeActivation(string fault) =>
        RunCustomEditorSta(() =>
        {
            var descriptor = Descriptor(false);
            var editor = new FakeEditor(descriptor, Defaults(descriptor));
            var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
            model.AlgorithmExecutionTimeoutText = "250";
            model.RefreshAsync().GetAwaiter().GetResult();
            var before = model.DraftContentHash;
            var factory = new CustomTestFactory(descriptor) { Fault = fault, AttemptWriteDuringCreate = true };
            var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(new[] { factory }));
            Assert.False(panel.TryUseCustomEditor());
            Assert.False(factory.InitialWrite!.Applied);
            Assert.Equal(before, model.DraftContentHash);
            Assert.Null(factory.Context!.Read());
            Assert.True(model.SaveAsync().GetAwaiter().GetResult()!.Saved);
            Assert.Equal(before, editor.LastSaveRequest!.Content.ContentHash);
            model.DisposeAsync().GetAwaiter().GetResult();
        });

    [Fact]
    public Task V129_H04_LateRefreshFailureRevokesContextAndDisposeFailureStillAllowsGenericSave() =>
        RunCustomEditorSta(() =>
        {
            var descriptor = Descriptor(false);
            var editor = new FakeEditor(descriptor, Defaults(descriptor));
            var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
            model.AlgorithmExecutionTimeoutText = "250";
            model.RefreshAsync().GetAwaiter().GetResult();
            var factory = new CustomTestFactory(descriptor);
            var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(new[] { factory }));
            Assert.True(panel.TryUseCustomEditor());
            var context = factory.Context!;
            var initial = context.Read()!;
            factory.Session!.ThrowRefresh = true;
            factory.Session.ThrowDispose = true;
            model.DisplayName = "触发受控刷新故障";
            Assert.Null(context.Read());
            Assert.False(context.TryReplace(initial.EditRevision, ReplaceCount(initial, 200)).Applied);
            Assert.Equal(Visibility.Visible, ((Border)panel.FindName("GenericEditorContainer")).Visibility);
            Assert.True(model.SaveAsync().GetAwaiter().GetResult()!.Saved);
            Assert.Equal(4, editor.LastSaveRequest!.Content.Configuration.Values.Single(value => value.Key == "Count").Value.AsInt64());
            model.DisposeAsync().GetAwaiter().GetResult();
        });

    [Fact]
    public Task V129_H05_UnloadContextSwitchAndReportedFaultRevokeExistingHandles() => RunCustomEditorSta(() =>
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var factory = new CustomTestFactory(descriptor);
        var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(new[] { factory }));
        Assert.True(panel.TryUseCustomEditor());
        var old = factory.Context!;
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Null(old.Read());
        Assert.True(panel.TryUseCustomEditor());
        old = factory.Context!;
        old.ReportFailure();
        Assert.Null(old.Read());
        Assert.Equal(Visibility.Visible, ((Border)panel.FindName("GenericEditorContainer")).Visibility);
        Assert.True(panel.TryUseCustomEditor());
        old = factory.Context!;
        var replacement = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        replacement.AlgorithmExecutionTimeoutText = "250";
        panel.DataContext = replacement;
        Assert.Null(old.Read());
        model.DisposeAsync().GetAwaiter().GetResult();
        replacement.DisposeAsync().GetAwaiter().GetResult();
    });

    private static Task RunCustomEditorSta(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public Task V129_H06_RefreshCallbacksCannotWriteBackIntoConfiguration() => RunCustomEditorSta(() =>
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var factory = new CustomTestFactory(descriptor);
        var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(new[] { factory }));
        Assert.True(panel.TryUseCustomEditor());
        var hash = model.ConfigurationContentHash;
        factory.Session!.AttemptWriteOnRefresh = true;
        model.ChangeReason = "显示刷新不能修改配置";
        Assert.NotNull(factory.Session.RefreshWrite);
        Assert.Equal("CustomEditorBusy", factory.Session.RefreshWrite!.ReasonCode);
        Assert.Equal(hash, model.ConfigurationContentHash);
        panel.UseGenericEditor();
        model.DisposeAsync().GetAwaiter().GetResult();
    });

    [Fact]
    public Task V129_H07_AlgorithmSelectionChangeRevokesOldContextBeforePropertyNotificationReentry() => RunCustomEditorSta(() =>
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        var context = model.CreateCustomEditorContext(() => { })!;
        context.Activate();
        bool? deniedDuringNotification = null;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.SelectedAlgorithm))
                deniedDuringNotification = context.Read() is null;
        };
        model.SelectedAlgorithm = new AlgorithmDescriptor(new AlgorithmIdentity("Different.Algorithm", "1"),
            descriptor.ConfigurationSchema, descriptor.ResultSchema);
        Assert.True(deniedDuringNotification);
        Assert.Null(context.Read());
        model.DisposeAsync().GetAwaiter().GetResult();
    });

    [Fact]
    public Task V129_H08_ParentedViewFailsMountAndFrozenAmbiguityCannotCollapseAfterDescriptorFailure() => RunCustomEditorSta(() =>
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var factory = new CustomTestFactory(descriptor) { Fault = "parented" };
        var panel = new RecipeDraftEditorPanel(model, new AlgorithmConfigurationEditorRegistry(new[] { factory }));
        var hash = model.DraftContentHash;
        Assert.False(panel.TryUseCustomEditor());
        Assert.Equal(hash, model.DraftContentHash);
        Assert.Null(factory.Context!.Read());
        var first = new CustomTestFactory(descriptor);
        var second = new CustomTestFactory(descriptor);
        var registry = new AlgorithmConfigurationEditorRegistry(new[] { first, second });
        second.ThrowDescriptor = true;
        panel = new RecipeDraftEditorPanel(model, registry);
        Assert.False(panel.TryUseCustomEditor());
        Assert.Equal(0, first.CreateCount);
        model.DisposeAsync().GetAwaiter().GetResult();
    });

    private sealed class CustomTestFactory : IAlgorithmConfigurationEditorFactory
    {
        internal CustomTestFactory(AlgorithmDescriptor descriptor) => DescriptorValue = new(
            new RecipeContractReference("Test.CustomEditor", "1", new string('D', 64)), descriptor.Identity,
            new RecipeContractReference(descriptor.ConfigurationSchema.Id, descriptor.ConfigurationSchema.Version,
                descriptor.ConfigurationSchema.ContentHash));
        public AlgorithmConfigurationEditorDescriptor Descriptor => ThrowDescriptor ? throw new InvalidOperationException("private diagnostic") : DescriptorValue;
        internal AlgorithmConfigurationEditorDescriptor DescriptorValue { get; set; }
        internal bool ThrowDescriptor { get; set; }
        internal string? Fault { get; set; }
        internal bool AttemptWriteDuringCreate { get; set; }
        internal AlgorithmConfigurationEditorEditResult? InitialWrite { get; private set; }
        internal int CreateCount { get; private set; }
        internal IAlgorithmConfigurationDraftEditContext? Context { get; private set; }
        internal CustomTestSession? Session { get; private set; }
        public IAlgorithmConfigurationEditorSession Create(IAlgorithmConfigurationDraftEditContext context)
        {
            CreateCount++;
            Context = context;
            if (AttemptWriteDuringCreate)
            {
                var initial = context.Read()!;
                InitialWrite = context.TryReplace(initial.EditRevision, ReplaceCount(initial, 99));
            }
            if (Fault == "reportedDuringCreate") context.ReportFailure();
            if (Fault == "create") throw new InvalidOperationException("private diagnostic");
            Session = new CustomTestSession(context)
            { NullView = Fault == "nullView", ThrowRefresh = Fault == "refresh", FailSecondRefresh = Fault == "secondRefresh" };
            if (Fault == "parented") Session.ParentContainer = new Border { Child = Session.ApplyButton };
            return Session;
        }
    }

    private sealed class CustomTestSession : IAlgorithmConfigurationEditorSession
    {
        private readonly IAlgorithmConfigurationDraftEditContext _context;
        internal CustomTestSession(IAlgorithmConfigurationDraftEditContext context)
        {
            _context = context;
            ApplyButton = new Button { Content = "Apply count" };
            ApplyButton.Click += (_, _) =>
            {
                var snapshot = context.Read()!;
                Assert.True(context.TryReplace(snapshot.EditRevision, ReplaceCount(snapshot, 31)).Applied);
            };
        }
        internal Button ApplyButton { get; }
        internal bool NullView { get; init; }
        internal bool ThrowRefresh { get; set; }
        internal bool FailSecondRefresh { get; init; }
        private int _refreshCount;
        internal bool ThrowDispose { get; set; }
        internal bool AttemptWriteOnRefresh { get; set; }
        internal AlgorithmConfigurationEditorEditResult? RefreshWrite { get; private set; }
        internal Border? ParentContainer { get; set; }
        public FrameworkElement View => NullView ? null! : ApplyButton;
        public void Refresh(AlgorithmConfigurationEditorSnapshot snapshot)
        {
            _refreshCount++;
            if (ThrowRefresh || (FailSecondRefresh && _refreshCount >= 2)) throw new InvalidOperationException("private diagnostic");
            if (AttemptWriteOnRefresh) RefreshWrite = _context.TryReplace(snapshot.EditRevision, ReplaceCount(snapshot, 88));
        }
        public void Dispose()
        { if (ThrowDispose) throw new InvalidOperationException("private diagnostic"); }
    }
}
