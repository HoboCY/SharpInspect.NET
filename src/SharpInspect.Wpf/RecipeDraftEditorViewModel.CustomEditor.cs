using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed partial class RecipeDraftEditorViewModel
{
    private long _configurationBufferGeneration;
    private long _configurationEditRevision;
    private bool _applyingCustomConfiguration;
    private bool _customInputRejected;
    private AlgorithmConfigurationDraftEditContext? _customEditContext;

    internal AlgorithmIdentity? CustomEditorAlgorithm => _selectedAlgorithm?.Identity;
    internal AlgorithmConfigurationSchema? CustomEditorSchema => _selectedAlgorithm?.ConfigurationSchema;

    internal AlgorithmConfigurationDraftEditContext? CreateCustomEditorContext(Action failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!_dispatcher.CheckAccess || _disposed || IsBusy || !HasDraft ||
            _selectedAlgorithm is null || !IsUsableSession(CurrentSession)) return null;
        _customEditContext?.Revoke();
        _customEditContext = new AlgorithmConfigurationDraftEditContext(this,
            _configurationBufferGeneration, CurrentSession, failure);
        return _customEditContext;
    }

    private void InvalidateCustomEditorBuffer()
    {
        _configurationBufferGeneration++;
        _configurationEditRevision++;
        _customEditContext?.MarkRevoked();
        _customEditContext = null;
        _customInputRejected = false;
    }

    internal sealed class AlgorithmConfigurationDraftEditContext : IAlgorithmConfigurationDraftEditContext
    {
        private readonly RecipeDraftEditorViewModel _owner;
        private readonly long _generation;
        private readonly InteractiveSession _session;
        private readonly AlgorithmIdentity _algorithm;
        private readonly AlgorithmConfigurationSchema _schema;
        private readonly Action _failure;
        private readonly CancellationTokenSource _lifetime = new();
        private int _lifetimeUsers = 1;
        private int _hostCallbackDepth;
        private bool _revoked;
        private bool _active;
        private bool _validating;

        internal AlgorithmConfigurationDraftEditContext(RecipeDraftEditorViewModel owner,
            long generation, InteractiveSession session, Action failure)
        {
            _owner = owner; _generation = generation; _session = session; _failure = failure;
            _algorithm = owner._selectedAlgorithm!.Identity;
            _schema = owner._selectedAlgorithm.ConfigurationSchema;
        }

        internal bool IsCurrent => !_revoked && !_owner._disposed &&
            ReferenceEquals(_owner._customEditContext, this) &&
            _generation == _owner._configurationBufferGeneration &&
            SameSession(_session, _owner.CurrentSession) &&
            _owner._selectedAlgorithm is { } current && current.Identity == _algorithm &&
            current.ConfigurationSchema.Id == _schema.Id &&
            current.ConfigurationSchema.Version == _schema.Version &&
            current.ConfigurationSchema.ContentHash == _schema.ContentHash;

        internal void Activate()
        {
            if (_owner._dispatcher.CheckAccess && IsCurrent) _active = true;
        }

        internal void MarkRevoked()
        {
            if (_revoked) return;
            _revoked = true; _active = false;
            // Validation cancellation callbacks must not execute on the UI thread.
            _ = Task.Run(() =>
            {
                try { _lifetime.Cancel(); } catch { }
                finally { ReleaseLifetime(); }
            });
        }

        private void ReleaseLifetime()
        { if (Interlocked.Decrement(ref _lifetimeUsers) == 0) _lifetime.Dispose(); }

        internal IDisposable EnterHostCallback()
        {
            _hostCallbackDepth++;
            return new HostCallbackScope(this);
        }

        private sealed class HostCallbackScope : IDisposable
        {
            private AlgorithmConfigurationDraftEditContext? _context;
            internal HostCallbackScope(AlgorithmConfigurationDraftEditContext context) => _context = context;
            public void Dispose()
            {
                var context = Interlocked.Exchange(ref _context, null);
                if (context is not null) context._hostCallbackDepth--;
            }
        }

        internal void Revoke()
        {
            MarkRevoked();
            if (!ReferenceEquals(_owner._customEditContext, this)) return;
            _owner._customEditContext = null;
            _owner._customInputRejected = false;
            _owner.RecomputeLocalValidation();
        }

        public AlgorithmConfigurationEditorSnapshot? Read()
        {
            if (!_owner._dispatcher.CheckAccess || !IsCurrent) return null;
            var values = new List<AlgorithmConfigurationEntry>();
            var issues = new List<AlgorithmValidationIssue>();
            foreach (var field in _owner._fields)
            {
                if (field.TryGetEntry(out var entry)) values.Add(entry);
                if (!field.IsValid) issues.Add(new(field.ValidationIssue ?? "AlgorithmFieldValueInvalid", field.Key));
            }
            var algorithm = _owner._selectedAlgorithm!;
            AlgorithmConfigurationSnapshot? configuration = null;
            if (issues.Count == 0)
            {
                try { configuration = AlgorithmConfigurationSnapshot.Create(algorithm.ConfigurationSchema, values); }
                catch { issues.Add(new("AlgorithmConfigurationInvalid")); }
            }
            if (_owner._customInputRejected) issues.Add(new("CustomEditorPendingInvalid"));
            return new(_owner._configurationEditRevision, algorithm.Identity, algorithm.ConfigurationSchema,
                values, configuration, issues, _owner._customInputRejected);
        }

        public AlgorithmConfigurationEditorEditResult TryReplace(long expectedEditRevision,
            IReadOnlyList<AlgorithmConfigurationEntry> completeValues)
        {
            if (!_owner._dispatcher.CheckAccess || !IsCurrent || !_active)
                return new(false, "CustomEditorContextUnavailable");
            if (_owner.IsBusy || _validating || _hostCallbackDepth != 0) return new(false, "CustomEditorBusy");
            if (expectedEditRevision != _owner._configurationEditRevision)
                return Reject("CustomEditorEditConflict");
            AlgorithmConfigurationSnapshot candidate;
            try
            {
                if (completeValues is null || completeValues.Count > 256)
                    return Reject("CustomEditorConfigurationInvalid");
                candidate = AlgorithmConfigurationSnapshot.Create(_owner._selectedAlgorithm!.ConfigurationSchema,
                    completeValues);
            }
            catch { return Reject("CustomEditorConfigurationInvalid"); }
            // The complete candidate has been checked before any observable field changes.
            var entries = candidate.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
            _owner._applyingCustomConfiguration = true;
            try
            {
                foreach (var field in _owner._fields)
                {
                    if (entries.TryGetValue(field.Key, out var value))
                    {
                        if (field.TryGetEntry(out var original) && original == value) continue;
                        field.SetValue(value.Value, RecipeDraftValueOrigin.Explicit);
                    }
                    else if (field.HasValue || !field.IsValid) field.ClearValue();
                }
                _owner._customInputRejected = false;
                _owner._configurationEditRevision++;
            }
            finally { _owner._applyingCustomConfiguration = false; }
            _owner.RecomputeLocalValidation();
            return new(true, "CustomEditorConfigurationApplied");
        }

        private AlgorithmConfigurationEditorEditResult Reject(string reason)
        {
            _owner._customInputRejected = true;
            _owner.RecomputeLocalValidation();
            return new(false, reason);
        }

        public async Task<RecipeDraftValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
        {
            if (!_owner._dispatcher.CheckAccess || !IsCurrent || !_active)
                return Failed("CustomEditorContextUnavailable");
            if (_owner.IsBusy || _validating || _hostCallbackDepth != 0) return Failed("CustomEditorBusy");
            if (cancellationToken.IsCancellationRequested) return Failed("CustomEditorValidationCanceled");
            var revision = _owner._configurationEditRevision;
            _validating = true;
            Interlocked.Increment(ref _lifetimeUsers);
            try
            {
                using var validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _lifetime.Token);
                var result = await _owner.ValidateCoreAsync(validationCancellation.Token).ConfigureAwait(true);
                if (!IsCurrent) return Failed("CustomEditorContextUnavailable");
                if (cancellationToken.IsCancellationRequested) return Failed("CustomEditorValidationCanceled");
                if (revision != _owner._configurationEditRevision) return Failed("CustomEditorEditConflict");
                return result;
            }
            catch { return Failed("CustomEditorValidationFailed"); }
            finally { _validating = false; ReleaseLifetime(); }
        }

        public void ReportFailure()
        {
            if (!_owner._dispatcher.CheckAccess || !IsCurrent) return;
            // Revoke before notifying consumer/UI code. The callback may immediately reenter.
            Revoke();
            try { _failure(); } catch { /* A failed presentation callback grants no capabilities. */ }
        }

        private static RecipeDraftValidationResult Failed(string reason) =>
            new(false, reason, Array.Empty<AlgorithmValidationIssue>());
    }
}
