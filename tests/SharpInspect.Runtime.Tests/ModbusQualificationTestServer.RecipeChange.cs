using System.Buffers.Binary;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Tests;

internal sealed partial class ModbusQualificationTestServer
{
    private ModbusRecipeChangeBinding? _recipeChangeBinding;
    private ModbusRecipeChangeControllerSignals _recipeChangeRequest = new(false, false, 0, 0);
    private ModbusRecipeChangeRuntimeSignals _recipeChangeResponse = new(false, 0, 0, 0, 0);
    private int _recipeChangeResponseReadCount;
    private (uint Sequence, uint Code)? _initializationRecipeRequest;
    private int _initializationPreviousResponseRequest;
    internal bool InitializationRecipeRequestInjected { get; private set; }

    // A synchronized sample contains at least three communication reads plus the
    // two dedicated blocks. Handshake initialization alone reads just those two
    // blocks. Inject after encoding its clear response, before sending it, so the
    // first observer sample deterministically sees the request. No runtime fields
    // or gate results are changed, and no spinning thread or retry is involved.
    internal void RequestRecipeChangeAfterHandshakeInitialization(uint sequence, uint code)
    {
        lock (_stateSync)
        {
            _initializationRecipeRequest = (sequence, code);
            _initializationPreviousResponseRequest = 0;
            InitializationRecipeRequestInjected = false;
        }
    }
    internal int RecipeChangeResponseReadCount => Volatile.Read(ref _recipeChangeResponseReadCount);
    internal ModbusRecipeChangeControllerSignals RecipeChangeController
    { get { lock (_stateSync) return _recipeChangeRequest; } }
    internal ModbusRecipeChangeRuntimeSignals RecipeChangeResponse
    { get { lock (_stateSync) return _recipeChangeResponse; } }
    internal bool PhysicalProductionReady { get { lock (_stateSync) return _productionReady; } }

    internal void RequestRecipeChange(uint sequence, uint code)
    { lock (_stateSync) _recipeChangeRequest = new(true, false, sequence, code); }
    internal void AcknowledgeRecipeChange()
    { lock (_stateSync) _recipeChangeRequest = _recipeChangeRequest with { Acknowledgement = true }; }
    internal void ResetRecipeChange()
    { lock (_stateSync) _recipeChangeRequest = new(false, false, 0, 0); }

    private bool TryReadRecipeChange(ModbusRequest request, out byte[] response)
    {
        response = Array.Empty<byte>();
        if (_recipeChangeBinding is not { } binding || request.Pdu.Length != 5) return false;
        var address = ReadUInt16(request.Pdu, 1);
        if (address != binding.ControllerStartAddress && address != binding.RuntimeStartAddress) return false;
        if (address == binding.RuntimeStartAddress) Interlocked.Increment(ref _recipeChangeResponseReadCount);
        ushort[] values;
        lock (_stateSync)
        {
            if (address == binding.ControllerStartAddress)
            {
                var value = _recipeChangeRequest;
                values = new[] { value.Request ? (ushort)1 : (ushort)0, value.Acknowledgement ? (ushort)1 : (ushort)0,
                    (ushort)(value.RequestSequence >> 16), (ushort)value.RequestSequence,
                    (ushort)(value.SelectionCode >> 16), (ushort)value.SelectionCode };
            }
            else
            {
                var value = _recipeChangeResponse;
                values = new[] { value.ResponseValid ? (ushort)1 : (ushort)0, value.Outcome, value.Reason,
                    (ushort)(value.RequestSequence >> 16), (ushort)value.RequestSequence,
                    (ushort)(value.SelectionCode >> 16), (ushort)value.SelectionCode };
                if (_initializationRecipeRequest is { } pending)
                {
                    var requests = RequestCount;
                    if (_initializationPreviousResponseRequest != 0 &&
                        requests - _initializationPreviousResponseRequest == 2)
                    {
                        _recipeChangeRequest = new(true, false, pending.Sequence, pending.Code);
                        _initializationRecipeRequest = null;
                        InitializationRecipeRequestInjected = true;
                    }
                    _initializationPreviousResponseRequest = requests;
                }
            }
        }
        if (ReadUInt16(request.Pdu, 3) != values.Length)
        { response = ExceptionResponse(request, 0x03, 0x03); return true; }
        var body = new byte[1 + values.Length * 2];
        body[0] = (byte)(values.Length * 2);
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1 + index * 2, 2), values[index]);
        response = Response(request, 0x03, body);
        return true;
    }

    private bool TryWriteRecipeChange(ushort address, ushort count, byte[] data)
    {
        if (_recipeChangeBinding is not { } binding || address != binding.RuntimeStartAddress ||
            count != ModbusRecipeChangeBinding.RuntimeRegisterCount) return false;
        lock (_stateSync)
            _recipeChangeResponse = new(BooleanRegister(data, 0),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2)),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10, 4)));
        return true;
    }
}
