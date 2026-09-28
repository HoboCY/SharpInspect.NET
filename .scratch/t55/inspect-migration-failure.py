import hashlib
import json
from pathlib import Path
import sqlite3

case = Path('F:/si55v3/ticket55/20260916-011049-854/reconcile-35/phase-6')
database = case / 'store.sqlite'
wal = case / 'store.sqlite-wal'
def hashes():
    return {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in (database, wal)}
before = hashes()
assert wal.stat().st_size == 0, 'Immutable cold read requires an empty retained WAL.'
with sqlite3.connect(database.as_uri() + '?mode=ro&immutable=1', uri=True) as connection:
    result = {
        'mode': 'ReadOnlyImmutableSqliteObservation',
        'signatureReverification': 'NotRun',
        'auditTail': list(connection.execute('SELECT Sequence,Kind,FactPosition FROM audit_entries WHERE Sequence>=14 ORDER BY Sequence')),
        'reconciliation': list(connection.execute('SELECT Position,Kind,AuditSequence FROM evidence_reconciliation_events ORDER BY Position')),
        'commands': list(connection.execute('SELECT Position,CommandKind,ReasonCode FROM command_facts ORDER BY Position')),
        'attempts': connection.execute('SELECT COUNT(*) FROM command_attempts').fetchone()[0],
        'commandFacts': connection.execute('SELECT COUNT(*) FROM command_facts').fetchone()[0],
        'internalAppendFailureReason': 'Unproven',
    }
after = hashes()
assert before == after, 'Original database or WAL changed during read-only inspection.'
result.update(databaseAndWalUnchanged=True, hashes=after)
Path('.scratch/t55/migration-failure-cold-observation.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
