using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed partial class RecipeDraftEditorTests
{
    [Fact]
    public async Task V129_C01_CompleteReplacementSharesGenericBufferPreservingUnchangedOriginsAndWholeDraft()
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        await using var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        var context = Assert.IsType<RecipeDraftEditorViewModel.AlgorithmConfigurationDraftEditContext>(
            model.CreateCustomEditorContext(() => { }));
        var initial = Assert.IsType<AlgorithmConfigurationEditorSnapshot>(context.Read());
        Assert.False(context.TryReplace(initial.EditRevision, initial.Values).Applied);
        context.Activate();
        var values = ReplaceCount(initial, 17);
        Assert.True(context.TryReplace(initial.EditRevision, values).Applied);
        values[0] = new("Unknown", "none", AlgorithmScalarValue.FromString("outside"));
        Assert.Equal("17", Assert.Single(model.Fields, field => field.Key == "Count").InputText);
        Assert.Equal(RecipeDraftValueOrigin.Explicit, Assert.Single(model.Fields, field => field.Key == "Count").Origin);
        Assert.Equal(RecipeDraftValueOrigin.AuthoringDefault, Assert.Single(model.Fields, field => field.Key == "Ratio").Origin);
        Assert.True((await context.ValidateAsync()).Valid);
        var validated = editor.LastValidatedContent!;
        Assert.Equal(model.ConfigurationContentHash, validated.Configuration.ContentHash);
        Assert.Equal("Primary", validated.CameraRole);
        Assert.Equal(TimeSpan.FromMilliseconds(250), validated.AlgorithmExecutionTimeout);
        context.Revoke();
        Assert.Null(context.Read());
        Assert.True((await model.SaveAsync())!.Saved);
        Assert.Equal(validated.ContentHash, editor.LastSaveRequest!.Content.ContentHash);
        Assert.Equal(1, editor.SaveCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unit")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("range")]
    [InlineData("capacity")]
    public async Task V129_C02_InvalidCompleteReplacementRetainsHashAndBlocksSavingUntilExplicitFallback(string scenario)
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        await using var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        var context = model.CreateCustomEditorContext(() => { })!;
        context.Activate();
        var initial = context.Read()!;
        var hash = model.DraftContentHash;
        var values = initial.Values.ToList();
        var count = values.FindIndex(value => value.Key == "Count");
        switch (scenario)
        {
            case "missing": values.RemoveAt(count); break;
            case "unit": values[count] = new("Count", "wrong", AlgorithmScalarValue.FromInt64(2)); break;
            case "unknown": values.Add(new("Unknown", "none", AlgorithmScalarValue.FromInt64(2))); break;
            case "duplicate": values.Add(values[count]); break;
            case "type": values[count] = new("Count", "items", AlgorithmScalarValue.FromString("2")); break;
            case "range":
                values[values.FindIndex(value => value.Key == "Ratio")] = new("Ratio", "ratio", AlgorithmScalarValue.FromFloat64(11));
                break;
            case "capacity": values = Enumerable.Repeat(values[count], 257).ToList(); break;
        }
        var result = context.TryReplace(initial.EditRevision, values);
        Assert.False(result.Applied);
        Assert.Equal("CustomEditorConfigurationInvalid", result.ReasonCode);
        Assert.Equal(hash, model.DraftContentHash);
        Assert.Equal(initial.Configuration!.ContentHash, context.Read()!.Configuration!.ContentHash);
        Assert.True(context.Read()!.PendingInvalid);
        Assert.False(model.CanSave);
        Assert.False((await context.ValidateAsync()).Valid);
        Assert.Null(await model.SaveAsync());
        Assert.Equal(0, editor.SaveCount);
        context.Revoke();
        Assert.True(model.CanSave);
        Assert.Equal(hash, model.DraftContentHash);
        Assert.True((await model.SaveAsync())!.Saved);
    }

    [Fact]
    public async Task V129_C03_StaleRevisionCannotOverwriteGenericEditAndFreshReplacementClearsPendingRejection()
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        await using var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var context = model.CreateCustomEditorContext(() => { })!;
        context.Activate();
        var old = context.Read()!;
        Assert.Single(model.Fields, field => field.Key == "Count").InputText = "19";
        Assert.Equal("CustomEditorEditConflict", context.TryReplace(old.EditRevision, ReplaceCount(old, 4)).ReasonCode);
        Assert.Equal("19", Assert.Single(model.Fields, field => field.Key == "Count").InputText);
        var current = context.Read()!;
        Assert.True(current.PendingInvalid);
        Assert.True(context.TryReplace(current.EditRevision, ReplaceCount(current, 20)).Applied);
        Assert.False(context.Read()!.PendingInvalid);
        Assert.True(model.IsValid);
    }

    [Fact]
    public async Task V129_C04_NewDraftSessionLockAndSaveReloadRevokeOldCapabilitiesBeforeTheyCanWrite()
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var old = model.CreateCustomEditorContext(() => { })!;
        old.Activate();
        var snapshot = old.Read()!;
        model.CreateNewDraft();
        Assert.False(old.TryReplace(snapshot.EditRevision, snapshot.Values).Applied);
        Assert.False((await old.ValidateAsync()).Valid);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        var savedContext = model.CreateCustomEditorContext(() => { })!;
        savedContext.Activate();
        Assert.True((await model.SaveAsync())!.Saved);
        Assert.Null(savedContext.Read());
        var lockedContext = model.CreateCustomEditorContext(() => { })!;
        lockedContext.Activate();
        sessions.Publish(new(InteractiveSessionState.Unauthenticated, null, null));
        Assert.Null(lockedContext.Read());
        Assert.False(lockedContext.TryReplace(snapshot.EditRevision, snapshot.Values).Applied);
        Assert.False(model.HasDraft);
    }

    [Fact]
    public async Task V129_C05_CustomSemanticValidationAndHostSaveUseTheSameCompleteContentAndRejection()
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        editor.ValidationOverride = (_, _) => ValueTask.FromResult(new RecipeDraftValidationResult(false,
            "RecipeDraftAlgorithmSemanticInvalid", new[] { new AlgorithmValidationIssue("CoupledFieldInvalid", "Count") }));
        await using var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        var context = model.CreateCustomEditorContext(() => { })!;
        context.Activate();
        var initial = context.Read()!;
        Assert.True(context.TryReplace(initial.EditRevision, ReplaceCount(initial, 22)).Applied);
        var validation = await context.ValidateAsync();
        Assert.False(validation.Valid);
        Assert.Equal("RecipeDraftAlgorithmSemanticInvalid", validation.ReasonCode);
        var customContent = editor.LastValidatedContent!.ContentHash;
        await model.ValidateAsync();
        Assert.Equal(customContent, editor.LastValidatedContent!.ContentHash);
        Assert.Null(await model.SaveAsync());
        Assert.Equal(customContent, editor.LastValidatedContent!.ContentHash);
        Assert.Equal(0, editor.SaveCount);
    }

    [Fact]
    public async Task V129_C06_BusyValidationRejectsConcurrentReplaceAndCancellationCannotReportSuccess()
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RecipeDraftValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.ValidationOverride = (_, token) => { entered.TrySetResult(true); return new(release.Task.WaitAsync(token)); };
        await using var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var context = model.CreateCustomEditorContext(() => { })!;
        context.Activate();
        var snapshot = context.Read()!;
        using var canceled = new CancellationTokenSource();
        var validating = context.ValidateAsync(canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("CustomEditorBusy", context.TryReplace(snapshot.EditRevision, ReplaceCount(snapshot, 30)).ReasonCode);
        canceled.Cancel();
        Assert.False((await validating).Valid);
        Assert.Equal(snapshot.Configuration!.ContentHash, context.Read()!.Configuration!.ContentHash);
    }

    [Fact]
    public void V129_C07_PublicExtensionCapabilityContainsOnlyConfigurationEditingAndValidation()
    {
        var capability = typeof(IAlgorithmConfigurationDraftEditContext);
        Assert.Equal(new[] { "Read", "ReportFailure", "TryReplace", "ValidateAsync" },
            capability.GetMethods().Select(method => method.Name).OrderBy(name => name));
        var reachable = new[] { capability, typeof(AlgorithmConfigurationEditorSnapshot),
            typeof(AlgorithmConfigurationEditorDescriptor), typeof(IAlgorithmConfigurationEditorFactory),
            typeof(IAlgorithmConfigurationEditorSession) };
        var forbidden = new[] { typeof(IServiceProvider), typeof(IRecipeDraftEditor),
            typeof(IInteractiveSessionService), typeof(RecipeDraftEditorViewModel) };
        foreach (var type in reachable)
        {
            var exposed = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType));
            Assert.DoesNotContain(exposed, candidate => forbidden.Contains(candidate));
        }
        Assert.DoesNotContain(typeof(AlgorithmConfigurationEditorSnapshot).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "SharpInspect.Runtime");
    }

    [Fact]
    public async Task V129_C08_RevocationCancelsOutstandingCustomValidationAndReleasesHostBusyState()
    {
        var descriptor = Descriptor(false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.ValidationOverride = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(true, "RecipeDraftValid", Array.Empty<AlgorithmValidationIssue>());
        };
        await using var model = NewModel(editor, new FakeSessions(Authenticated()), descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        var context = model.CreateCustomEditorContext(() => { })!;
        context.Activate();
        var validating = context.ValidateAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsBusy);
        context.Revoke();
        Assert.False((await validating.WaitAsync(TimeSpan.FromSeconds(5))).Valid);
        Assert.False(model.IsBusy);
        Assert.Null(context.Read());
    }

    private static AlgorithmConfigurationEntry[] ReplaceCount(AlgorithmConfigurationEditorSnapshot snapshot, long value) =>
        snapshot.Values.Select(entry => entry.Key == "Count"
            ? new AlgorithmConfigurationEntry(entry.Key, entry.Unit, AlgorithmScalarValue.FromInt64(value)) : entry).ToArray();
}
