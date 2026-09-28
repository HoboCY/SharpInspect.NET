from pathlib import Path
p=Path('src/SharpInspect.Runtime/Performance/PerformanceCaptureBuffer.cs')
s=p.read_text(encoding='utf-8-sig')
s=s.replace('    private long _bytes, _sequence, _drops, _firstMissing;', '    private long _bytes, _sequence, _drops, _firstMissing;\n    private int _activeObservers, _retirementIncomplete;')
s=s.replace('    internal bool IsSealed =>', '    internal bool RetirementIncomplete => Volatile.Read(ref _retirementIncomplete) != 0;\n    internal bool IsSealed =>')
# Guard includes the entire loss-accounting path, including TryEnter failure.
for name in ['Event','Sample','Frame','Binding']:
    a=s.index('    internal void '+name+'(')
    b=s.index('    {',a)
    depth=1; i=b+5
    while depth:
        if s[i]=='{': depth+=1
        elif s[i]=='}': depth-=1
        i+=1
    body=s[b+6:i-1]
    assert '        if (IsSealed) return;' in body
    body=body.replace('        if (IsSealed) return;','        if (!TryBeginObservation()) return;\n        try\n        {',1)
    s=s[:b+6]+body+'        }\n        finally { EndObservation(); }\n    '+s[i-1:]
s=s.replace('    internal void ObservationLost() => Drop(Interlocked.Read(ref _sequence) + 1);', '''    internal void ObservationLost()
    {
        if (!TryBeginObservation()) return;
        try { Drop(Interlocked.Read(ref _sequence) + 1); }
        finally { EndObservation(); }
    }

    internal bool TryBeginObservation()
    {
        if (IsSealed) return false;
        Interlocked.Increment(ref _activeObservers);
        if (!IsSealed) return true;
        EndObservation();
        return false;
    }
    internal void EndObservation() => Interlocked.Decrement(ref _activeObservers);''')
s=s.replace('        lock (_sync) Volatile.Write(ref _sealed, 1);', '''        lock (_sync)
        {
            Volatile.Write(ref _sealed, 1);
            // A producer paused anywhere before its finally may still account a drop. Do not wait
            // on that producer and do not claim complete evidence. This uncertainty is sticky.
            if (Volatile.Read(ref _activeObservers) != 0) Volatile.Write(ref _retirementIncomplete, 1);
        }''')
p.write_text(s,encoding='utf-8')
