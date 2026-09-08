using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class RecipeDraftEditorTests
{
    [Fact]
    public async Task V115_W01_TypedFieldsPreserveOriginsAndOptionalPresence()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var editor = new FakeEditor(descriptor,
            new[]
            {
                new AlgorithmConfigurationEntry("Enabled", "bool", AlgorithmScalarValue.FromBoolean(true)),
                new AlgorithmConfigurationEntry("Count", "items", AlgorithmScalarValue.FromInt64(4)),
                new AlgorithmConfigurationEntry("Ratio", "ratio", AlgorithmScalarValue.FromFloat64(1.25)),
                new AlgorithmConfigurationEntry("Mode", "name", AlgorithmScalarValue.FromString("Auto")),
                new AlgorithmConfigurationEntry("Tag", "tag", AlgorithmScalarValue.FromEnum("Initial"))
            });
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        model.AlgorithmExecutionTimeoutText = "250";

        var enabled = model.Fields.Single(field => field.Key == "Enabled");
        var count = model.Fields.Single(field => field.Key == "Count");
        var ratio = model.Fields.Single(field => field.Key == "Ratio");
        var mode = model.Fields.Single(field => field.Key == "Mode");
        var tag = model.Fields.Single(field => field.Key == "Tag");

        Assert.Equal(RecipeDraftValueOrigin.AuthoringDefault, enabled.Origin);
        Assert.False(enabled.HasValue && enabled.BooleanInput == false);
        enabled.BooleanInput = false;
        count.InputText = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ratio.InputText = "1.5";
        mode.ChoiceValue = "Manual";
        tag.InputText = "扩展标签🙂";

        Assert.True(enabled.HasValue);
        Assert.False(enabled.BooleanInput);
        Assert.Equal(RecipeDraftValueOrigin.Explicit, enabled.Origin);
        Assert.Equal(RecipeDraftValueOrigin.Explicit, count.Origin);
        Assert.Equal(RecipeDraftValueOrigin.Explicit, mode.Origin);
        Assert.False(tag.HasChoiceValues);
        Assert.True(model.IsValid);

        await model.ValidateAsync();

        Assert.NotNull(editor.LastValidatedContent);
        var values = editor.LastValidatedContent!.Configuration.Values.ToDictionary(value => value.Key);
        Assert.False(values["Enabled"].Value.AsBoolean());
        Assert.Equal(long.MaxValue, values["Count"].Value.AsInt64());
        Assert.Equal(1.5, values["Ratio"].Value.AsFloat64());
        Assert.Equal("Manual", values["Mode"].Value.AsString());
        Assert.Equal("扩展标签🙂", values["Tag"].Value.AsEnum());
        Assert.Equal(RecipeDraftValueOrigin.Explicit,
            editor.LastValidatedContent.ValueOrigins.Single(origin => origin.Key == "Enabled").Origin);
    }

    [Fact]
    public async Task V115_W02_EmptyAllowedValuesAndMissingRequiredStayInvalid()
    {
        var descriptor = Descriptor(includeEmptyChoice: true);
        var editor = new FakeEditor(descriptor, Array.Empty<AlgorithmConfigurationEntry>());
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);

        var enumField = model.Fields.Single(field => field.Key == "Tag");
        Assert.True(enumField.HasChoiceValues);
        Assert.Empty(enumField.AllowedValues);
        Assert.False(enumField.HasValue);
        Assert.False(model.IsValid);
        Assert.Contains(model.ValidationIssues, issue => issue.Code == "AlgorithmFieldMissingRequired");

        await model.ValidateAsync();

        Assert.Null(editor.LastValidatedContent);
        Assert.Contains("RecipeDraftInputInvalid", model.ValidationReasonCode);
    }

    [Fact]
    public async Task V115_W03_UnauthorizedAndStepUpAccessNeverSave()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor))
        {
            Access = new RecipeDraftAccess(false, "private-provider-detail", false)
        };
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        Assert.False(model.CanSave);
        Assert.Null(await model.SaveAsync());
        Assert.Equal(0, editor.SaveCount);
        Assert.Equal("RecipeDraftAccessDenied", model.ErrorCode);

        editor.Access = new RecipeDraftAccess(true, "RecipeDraftAccessGranted", true);
        await model.RefreshAsync();
        Assert.False(model.CanSave);
        Assert.Null(await model.SaveAsync());
        Assert.Equal(0, editor.SaveCount);
        Assert.Equal("RecipeDraftStepUpRequired", model.ErrorCode);
    }

    [Fact]
    public async Task V115_W04_SaveReReadsRevisionAndKeepsExplicitOrigin()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        var field = model.Fields.Single(item => item.Key == "Count");
        field.InputText = "9";

        var result = await model.SaveAsync();

        Assert.NotNull(result);
        Assert.True(result!.Saved);
        Assert.Equal(1, editor.SaveCount);
        Assert.True(editor.ReadCount >= 1);
        Assert.NotNull(model.CurrentRevision);
        Assert.Equal(result.Revision!.RevisionContentHash, model.RevisionContentHash);
        Assert.Equal(9, editor.LastValidatedContent!.Configuration.Values
            .Single(item => item.Key == "Count").Value.AsInt64());
        Assert.Equal(RecipeDraftValueOrigin.Explicit,
            model.Fields.Single(item => item.Key == "Count").Origin);
        Assert.False(model.CanRelease);
        Assert.Equal("NotRun", model.DependenciesStatus);
    }

    [Fact]
    public async Task V115_W05_SessionChangeClearsDraftAndRejectsLateQuery()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var queryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryRelease = new TaskCompletionSource<RecipeDraftPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new FakeEditor(descriptor, Defaults(descriptor))
        {
            QueryOverride = _ =>
            {
                queryEntered.TrySetResult(true);
                return queryRelease.Task;
            }
        };
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        var refresh = model.RefreshAsync();
        await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        sessions.Publish(new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null));
        queryRelease.TrySetResult(new RecipeDraftPage(true, "RecipeDraftHistoryAvailable",
            Array.AsReadOnly(Array.Empty<RecipeDraftRevision>()), 0, null));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(model.HasDraft);
        Assert.Empty(model.History);
        Assert.Equal(string.Empty, model.RecipeKey);
        Assert.Equal(string.Empty, model.DisplayName);
        Assert.Equal(string.Empty, model.CameraRole);
        Assert.Equal(string.Empty, model.AlgorithmExecutionTimeoutText);
        Assert.Equal(string.Empty, model.ChangeReason);
        Assert.Equal(string.Empty, model.ExposureTimeUsText);
        Assert.Equal(string.Empty, model.GainDbText);
        Assert.Equal(string.Empty, model.RoiOffsetXText);
        Assert.Equal(string.Empty, model.RoiOffsetYText);
        Assert.Equal(string.Empty, model.RoiWidthText);
        Assert.Equal(string.Empty, model.RoiHeightText);
        Assert.Equal(string.Empty, model.AcquisitionTimeoutMsText);
        Assert.Equal(string.Empty, model.TriggerDelayUsText);
        Assert.Equal(string.Empty, model.WhiteBalanceRedText);
        Assert.Equal(string.Empty, model.WhiteBalanceGreenText);
        Assert.Equal(string.Empty, model.WhiteBalanceBlueText);
        Assert.Equal("RecipeDraftUnauthenticated", model.ErrorCode);
        Assert.DoesNotContain("private", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V115_W06_ActualHiddenPanelLoadsTypedEditorWithoutJsonSurface()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var descriptor = Descriptor(includeEmptyChoice: false);
                var editor = new FakeEditor(descriptor, Defaults(descriptor));
                var sessions = new FakeSessions(Authenticated());
                var model = NewModel(editor, sessions, descriptor);
                var panel = new RecipeDraftEditorPanel(model);
                window = new Window
                {
                    Content = panel, Width = 1000, Height = 900, ShowInTaskbar = false,
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();
                Assert.True(panel.ActualHeight > 0);
                Assert.Contains(panel.ViewModel.Fields, field => field.HasChoiceValues);
                panel.ClearSensitiveInputs();
                Assert.False(panel.ViewModel.HasDraft);
                model.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V115_W10_OverlongUtf8PasteIsRejectedWithoutRetainingPayload()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            RecipeDraftEditorViewModel? model = null;
            try
            {
                var descriptor = Descriptor(includeEmptyChoice: false);
                var editor = new FakeEditor(descriptor, Defaults(descriptor));
                var sessions = new FakeSessions(Authenticated());
                model = NewModel(editor, sessions, descriptor);
                model.AlgorithmExecutionTimeoutText = "250";
                model.RefreshAsync().GetAwaiter().GetResult();
                Assert.True(model.IsValid);
                var panel = new RecipeDraftEditorPanel(model);
                window = new Window
                {
                    Content = panel, Width = 1000, Height = 900, ShowInTaskbar = false,
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();
                var field = model.Fields.Single(item => item.Key == "Tag");
                var box = FindVisualChildren<TextBox>(panel).Single(item =>
                    item.DataContext is RecipeDraftFieldViewModel candidate && candidate.Key == "Tag");
                box.Text = new string('中', 2000); // 6000 UTF-8 bytes, below the XAML char helper cap.
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

                Assert.Equal(string.Empty, box.Text);
                Assert.Equal(string.Empty, field.InputText);
                Assert.False(field.HasValue);
                Assert.Equal("AlgorithmFieldInputBoundsInvalid", field.ValidationIssue);
                Assert.False(model.IsValid);
                Assert.False(model.CanSave);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                if (model is not null) model.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V115_W11_TopLevelCameraAndRequirementInputsStayBounded()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        Assert.True(model.IsValid);

        model.RecipeKey = new string('R', 65);
        Assert.Equal(string.Empty, model.RecipeKey);
        Assert.False(model.CanSave);
        model.RecipeKey = "Recipe-1";

        model.DisplayName = new string('中', 129);
        Assert.Equal(string.Empty, model.DisplayName);
        Assert.False(model.CanSave);
        model.DisplayName = "Display";

        model.AlgorithmExecutionTimeoutText = new string('1', 65);
        Assert.Equal(string.Empty, model.AlgorithmExecutionTimeoutText);
        Assert.False(model.CanSave);
        model.AlgorithmExecutionTimeoutText = "250";

        model.WhiteBalanceRedText = new string('中', 100);
        Assert.Equal(string.Empty, model.WhiteBalanceRedText);
        Assert.False(model.CanSave);
        model.WhiteBalanceRedText = "1";
        model.WhiteBalanceGreenText = "1";
        model.WhiteBalanceBlueText = "1";

        model.AddAssetRequirement();
        var asset = Assert.Single(model.AssetRequirements);
        asset.Role = new string('中', 100);
        Assert.Equal(string.Empty, asset.Role);
        Assert.False(model.CanSave);
    }

    [Fact]
    public async Task V115_W12_NativePasteIsRejectedBeforeMaxLengthCanTruncate()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            RecipeDraftEditorViewModel? model = null;
            try
            {
                var descriptor = Descriptor(includeEmptyChoice: false, optionalTextMaximumLength: 24);
                var editor = new FakeEditor(descriptor, Defaults(descriptor));
                var sessions = new FakeSessions(Authenticated());
                model = NewModel(editor, sessions, descriptor);
                model.AlgorithmExecutionTimeoutText = "250";
                var field = model.Fields.Single(item => item.Key == "OptionalText");
                field.InputText = "seed";
                model.RefreshAsync().GetAwaiter().GetResult();
                Assert.True(model.IsValid);
                var panel = new RecipeDraftEditorPanel(model);
                window = new Window
                {
                    Content = panel, Width = 1000, Height = 900, ShowInTaskbar = false,
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();
                var box = FindVisualChildren<TextBox>(panel).Single(item =>
                    item.DataContext is RecipeDraftFieldViewModel candidate && candidate.Key == "OptionalText");
                Assert.Equal(24, box.MaxLength);
                box.SelectAll();
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, new string('A', 25));
                var args = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
                box.RaiseEvent(args);

                Assert.True(args.CommandCancelled);
                Assert.Equal(string.Empty, box.Text);
                Assert.Equal(string.Empty, field.InputText);
                Assert.Equal("AlgorithmFieldInputBoundsInvalid", field.ValidationIssue);
                Assert.False(model.CanSave);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                if (model is not null) model.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V115_W13_PastingPreflightRejectsUtf8OverflowAndInvalidSurrogate()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            RecipeDraftEditorViewModel? model = null;
            try
            {
                var descriptor = Descriptor(includeEmptyChoice: false);
                var editor = new FakeEditor(descriptor, Defaults(descriptor));
                var sessions = new FakeSessions(Authenticated());
                model = NewModel(editor, sessions, descriptor);
                model.AlgorithmExecutionTimeoutText = "250";
                var field = model.Fields.Single(item => item.Key == "Tag");
                field.InputText = "seed";
                model.RefreshAsync().GetAwaiter().GetResult();
                var panel = new RecipeDraftEditorPanel(model);
                window = new Window
                {
                    Content = panel, Width = 1000, Height = 900, ShowInTaskbar = false,
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();
                var box = FindVisualChildren<TextBox>(panel).Single(item =>
                    item.DataContext is RecipeDraftFieldViewModel candidate && candidate.Key == "Tag");

                box.SelectAll();
                var invalidSurrogate = new DataObject();
                invalidSurrogate.SetData(DataFormats.UnicodeText, "\uD800");
                var invalidArgs = new DataObjectPastingEventArgs(invalidSurrogate, false, DataFormats.UnicodeText);
                box.RaiseEvent(invalidArgs);
                Assert.True(invalidArgs.CommandCancelled);
                Assert.Equal(string.Empty, field.InputText);

                field.InputText = "seed";
                box.SelectAll();
                var utf8Overflow = new DataObject();
                utf8Overflow.SetData(DataFormats.UnicodeText, new string('中', 2000));
                var utf8Args = new DataObjectPastingEventArgs(utf8Overflow, false, DataFormats.UnicodeText);
                box.RaiseEvent(utf8Args);
                Assert.True(utf8Args.CommandCancelled);
                Assert.Equal(string.Empty, box.Text);
                Assert.Equal(string.Empty, field.InputText);
                Assert.Equal("AlgorithmFieldInputBoundsInvalid", field.ValidationIssue);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                if (model is not null) model.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V115_W07_StepUpFreezesBindingAndContentUntilSave()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var editor = new FakeEditor(descriptor, Defaults(descriptor))
        {
            Access = new RecipeDraftAccess(true, "RecipeDraftAccessGranted", true)
        };
        var stepUpEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepUpRelease = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepUp = new FakeStepUp(request =>
        {
            stepUpEntered.TrySetResult(true);
            return stepUpRelease.Task;
        });
        var sessions = new FakeSessions(Authenticated());
        await using var model = new RecipeDraftEditorViewModel(editor, sessions, new InlineUiDispatcher(), null, stepUp);
        model.SelectedAlgorithm = descriptor;
        model.AlgorithmExecutionTimeoutText = "250";
        await model.RefreshAsync();
        var count = model.Fields.Single(field => field.Key == "Count");
        count.InputText = "7";

        var save = model.SaveWithStepUpAsync("temporary-password");
        await stepUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        count.InputText = "99";
        var operationId = model.LastStepUpBinding!.CommandCorrelationId;
        stepUpRelease.TrySetResult(new StepUpResult(true, "StepUpAccepted",
            Guid.Parse("00000000-0000-0000-0000-000000000003")));
        var result = await save.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result!.Saved);
        Assert.NotNull(stepUp.LastRequest);
        Assert.Equal(Permission.EditRecipeDraft, stepUp.LastRequest!.Binding.Permission);
        Assert.Equal(AuditedCommandKind.SaveRecipeDraft, stepUp.LastRequest.Binding.CommandKind);
        Assert.Equal(model.DraftId.ToString("D"), stepUp.LastRequest.Binding.TargetId);
        Assert.Equal(operationId, stepUp.LastRequest.CorrelationId);
        Assert.Equal(operationId, editor.LastSaveRequest!.OperationId);
        Assert.Equal(7, editor.LastSaveRequest.Content.Configuration.Values
            .Single(value => value.Key == "Count").Value.AsInt64());
        Assert.Equal(stepUp.LastResult!.GrantId, editor.LastSaveRequest.StepUpGrantId);
        Assert.Equal(stepUp.LastResult.GrantId, editor.LastSaveRequest.Invocation.StepUpGrantId);
    }

    [Fact]
    public async Task V115_W08_HistoryUsesFixedWatermarkAndBoundedNextPages()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var content = Content(descriptor);
        var revisions = Enumerable.Range(1, 21).Select(index => new RecipeDraftRevision(
            index, Guid.Parse("00000000-0000-0000-0000-000000000010"), index, Guid.NewGuid(),
            index == 1 ? null : new string('A', 64), new string((char)('A' + index % 20), 64), content,
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"), 1, "history", DateTimeOffset.UtcNow)).ToArray();
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        editor.QueryOverride = filter =>
        {
            var page = filter.AfterPosition switch
            {
                0 => (revisions.Take(10).ToArray(), 21L, (long?)10),
                10 => (revisions.Skip(10).Take(10).ToArray(), 21L, (long?)20),
                20 => (revisions.Skip(20).ToArray(), 21L, (long?)null),
                _ => (Array.Empty<RecipeDraftRevision>(), 21L, (long?)null)
            };
            editor.ObservedFilters.Add(filter);
            return Task.FromResult(new RecipeDraftPage(true, "RecipeDraftHistoryAvailable",
                Array.AsReadOnly(page.Item1), page.Item2, page.Item3));
        };
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);
        await model.RefreshAsync();
        Assert.Equal(10, model.History.Count);
        Assert.Equal(21, model.HistoryThroughPosition);
        Assert.True(model.CanNextHistory);
        await model.NextHistoryAsync();
        await model.NextHistoryAsync();
        Assert.Equal(21, model.History.Count);
        Assert.Equal(3, model.HistoryPage);
        Assert.False(model.CanNextHistory);
        Assert.Equal(new long[] { 0, 10, 20 }, editor.ObservedFilters.Select(filter => filter.AfterPosition));
        Assert.All(editor.ObservedFilters.Skip(1), filter => Assert.Equal(21, filter.ThroughPosition));
    }

    [Fact]
    public async Task V115_W09_MalformedCursorKeepsVerifiedPageAndDisablesFurtherPaging()
    {
        var descriptor = Descriptor(includeEmptyChoice: false);
        var content = Content(descriptor);
        var revisions = Enumerable.Range(1, 20).Select(index => new RecipeDraftRevision(
            index, Guid.Parse("00000000-0000-0000-0000-000000000011"), index, Guid.NewGuid(),
            index == 1 ? null : new string('B', 64), new string((char)('A' + index % 20), 64), content,
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"), 1, "history", DateTimeOffset.UtcNow)).ToArray();
        var editor = new FakeEditor(descriptor, Defaults(descriptor));
        editor.QueryOverride = filter =>
        {
            var page = filter.AfterPosition == 0
                ? (revisions.Take(10).ToArray(), 20L, (long?)10)
                // The rows end at 20, but the provider lies with cursor 5.
                : (revisions.Skip(10).Take(10).ToArray(), 20L, (long?)5);
            editor.ObservedFilters.Add(filter);
            return Task.FromResult(new RecipeDraftPage(true, "RecipeDraftHistoryAvailable",
                Array.AsReadOnly(page.Item1), page.Item2, page.Item3));
        };
        var sessions = new FakeSessions(Authenticated());
        await using var model = NewModel(editor, sessions, descriptor);

        await model.RefreshAsync();
        Assert.Equal(10, model.History.Count);
        Assert.Equal(10, model.HistoryNextAfterPosition);
        await model.NextHistoryAsync();

        Assert.Equal(10, model.History.Count);
        Assert.Null(model.HistoryNextAfterPosition);
        Assert.False(model.CanNextHistory);
        Assert.Equal("RecipeDraftQueryFailed", model.ErrorCode);
        await model.NextHistoryAsync();
        Assert.Equal(new long[] { 0, 10 }, editor.ObservedFilters.Select(filter => filter.AfterPosition));
    }

    private static RecipeDraftEditorViewModel NewModel(FakeEditor editor, FakeSessions sessions,
        AlgorithmDescriptor descriptor)
    {
        var model = new RecipeDraftEditorViewModel(editor, sessions, new InlineUiDispatcher());
        model.SelectedAlgorithm = descriptor;
        return model;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private static AlgorithmDescriptor Descriptor(bool includeEmptyChoice, int? optionalTextMaximumLength = null)
    {
        var fields = new List<AlgorithmFieldDefinition>
        {
            new("Enabled", AlgorithmScalarType.Boolean, "bool", false, null,
                AlgorithmScalarValue.FromBoolean(true), "可选布尔值；false 与缺失不同。"),
            new("Count", AlgorithmScalarType.Int64, "items", true,
                new AlgorithmScalarConstraints(minInt64: long.MinValue, maxInt64: long.MaxValue),
                AlgorithmScalarValue.FromInt64(4), "整数不会经过 double。"),
            new("Ratio", AlgorithmScalarType.Float64, "ratio", true,
                new AlgorithmScalarConstraints(minFloat64: -10, maxFloat64: 10),
                AlgorithmScalarValue.FromFloat64(1.25), "有限浮点值。"),
            new("Mode", AlgorithmScalarType.String, "name", true,
                new AlgorithmScalarConstraints(allowedValues: new[] { "Auto", "Manual" }),
                AlgorithmScalarValue.FromString("Auto"), "只能选择 Schema 字面值。"),
            new("Tag", AlgorithmScalarType.Enum, "tag", includeEmptyChoice,
                includeEmptyChoice ? new AlgorithmScalarConstraints(allowedValues: Array.Empty<string>()) : null,
                includeEmptyChoice ? null : AlgorithmScalarValue.FromEnum("Initial"),
                "无 AllowedValues 的 Enum 仍可输入字面值。"),
            new("OptionalText", AlgorithmScalarType.String, "text", false,
                optionalTextMaximumLength is { } optionalMaximum
                    ? new AlgorithmScalarConstraints(maxLength: optionalMaximum) : null)
        };
        var config = new AlgorithmConfigurationSchema("Recipe.Config", "1", fields);
        var overlay = new OverlayContract("Recipe.Overlay", "1");
        var result = new AlgorithmResultSchema("Recipe.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            Array.Empty<string>(), overlay);
        return new AlgorithmDescriptor(new AlgorithmIdentity("Recipe.Algorithm", "1"), config, result);
    }

    private static IReadOnlyList<AlgorithmConfigurationEntry> Defaults(AlgorithmDescriptor descriptor) =>
        descriptor.ConfigurationSchema.Fields.Where(field => field.AuthoringDefault is not null)
            .Select(field => new AlgorithmConfigurationEntry(field.Key, field.Unit, field.AuthoringDefault!))
            .ToArray();

    private static RecipeDraftContent Content(AlgorithmDescriptor descriptor)
    {
        var entries = Defaults(descriptor);
        var configuration = AlgorithmConfigurationSnapshot.Create(descriptor.ConfigurationSchema, entries);
        var origins = entries.Select(entry => new RecipeDraftFieldOrigin(entry.Key,
            RecipeDraftValueOrigin.AuthoringDefault));
        return new RecipeDraftContent("HistoryRecipe", "History Recipe",
            RecipeAlgorithmBinding.FromDescriptor(descriptor), configuration, "Primary",
            new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
                new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 1000, 0, null),
            TimeSpan.FromMilliseconds(250), Array.Empty<RecipeAssetRequirement>(),
            Array.Empty<RecipePolicyRequirement>(), origins);
    }

    private static InteractiveSession Authenticated() =>
        new(InteractiveSessionState.Authenticated, "00000000-0000-0000-0000-000000000001",
            Guid.Parse("00000000-0000-0000-0000-000000000002"));

    private sealed class FakeEditor : IRecipeDraftEditor
    {
        private readonly AlgorithmDescriptor _descriptor;
        private readonly IReadOnlyList<AlgorithmConfigurationEntry> _defaults;
        private RecipeDraftRevision? _stored;

        public FakeEditor(AlgorithmDescriptor descriptor, IReadOnlyList<AlgorithmConfigurationEntry> defaults)
        {
            _descriptor = descriptor;
            _defaults = defaults;
        }

        public IReadOnlyList<AlgorithmDescriptor> Algorithms => new[] { _descriptor };
        public RecipeDraftAccess Access { get; set; } = new(true, "RecipeDraftAccessGranted", false);
        public int SaveCount { get; private set; }
        public int ReadCount { get; private set; }
        public RecipeDraftContent? LastValidatedContent { get; private set; }
        public RecipeDraftSaveRequest? LastSaveRequest { get; private set; }
        public Func<RecipeDraftFilter, Task<RecipeDraftPage>>? QueryOverride { get; set; }
        public List<RecipeDraftFilter> ObservedFilters { get; } = new();

        public IReadOnlyList<AlgorithmConfigurationEntry> GetAuthoringDefaults(AlgorithmIdentity algorithm) => _defaults;
        public ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Access);
        public ValueTask<RecipeDraftValidationResult> ValidateAsync(RecipeDraftContent content, CancellationToken cancellationToken = default)
        {
            LastValidatedContent = content;
            return ValueTask.FromResult(new RecipeDraftValidationResult(true, "RecipeDraftValid",
                Array.Empty<AlgorithmValidationIssue>()));
        }
        public ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            LastSaveRequest = request;
            _stored = new RecipeDraftRevision(1, request.DraftId, 1, request.OperationId, null,
                new string('A', 64), request.Content, Guid.Parse("00000000-0000-0000-0000-000000000001"),
                request.Invocation.SessionId!.Value, 4, request.ChangeReason, DateTimeOffset.UtcNow);
            return ValueTask.FromResult(new RecipeDraftSaveResult(true, "RecipeDraftSaved", _stored,
                Array.Empty<AlgorithmValidationIssue>()));
        }
        public ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult(_stored is null
                ? new RecipeDraftReadResult(false, "RecipeDraftNotFound", null)
                : new RecipeDraftReadResult(true, "RecipeDraftRead", _stored));
        }
        public async ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter, CancellationToken cancellationToken = default)
        {
            if (QueryOverride is not null) return await QueryOverride(filter).ConfigureAwait(false);
            var revisions = _stored is null ? Array.Empty<RecipeDraftRevision>() : new[] { _stored };
            return new RecipeDraftPage(true, "RecipeDraftHistoryAvailable",
                Array.AsReadOnly(revisions), revisions.Length == 0 ? 0 : revisions[^1].Position, null);
        }
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        private readonly Func<StepUpRequest, Task<StepUpResult>> _authenticate;
        public FakeStepUp(Func<StepUpRequest, Task<StepUpResult>> authenticate) => _authenticate = authenticate;
        public StepUpRequest? LastRequest { get; private set; }
        public StepUpResult? LastResult { get; private set; }
        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastResult = await _authenticate(request).WaitAsync(cancellationToken).ConfigureAwait(false);
            return LastResult;
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;
        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed { add => _changed += value; remove => _changed -= value; }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(InteractiveSession session)
        {
            Current = session;
            _changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }
    }
}
