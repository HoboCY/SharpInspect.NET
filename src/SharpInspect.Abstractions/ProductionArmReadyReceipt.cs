using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable observation of the first physical Ready acknowledgement under one
/// authorized arm scope. It records history, not the station's current permission.
/// </summary>
public sealed class ProductionArmReadyReceipt
{
    internal ProductionArmReadyReceipt(Guid attemptId, Guid runtimeEpoch, string authorizationEventHash,
        RecipeActivationReference? activation, string maintenanceHeadHash, long admissionGeneration,
        uint controllerEpoch, long connectionGeneration, DateTimeOffset observedAtUtc)
    {
        AttemptId = RecipeActivationValidation.RequiredGuid(attemptId, nameof(attemptId));
        RuntimeEpoch = RecipeActivationValidation.RequiredGuid(runtimeEpoch, nameof(runtimeEpoch));
        AuthorizationEventHash = RecipeActivationValidation.Hash(authorizationEventHash, nameof(authorizationEventHash));
        Activation = RecipeActivationValidation.Reference(activation);
        MaintenanceHeadHash = RecipeActivationValidation.Hash(maintenanceHeadHash, nameof(maintenanceHeadHash));
        if (admissionGeneration < 0 || controllerEpoch == 0 || connectionGeneration < 1)
            throw new ArgumentException("ProductionArmReadyReceiptScopeInvalid");
        AdmissionGeneration = admissionGeneration;
        ControllerEpoch = controllerEpoch;
        ConnectionGeneration = connectionGeneration;
        ObservedAtUtc = RecipeActivationValidation.Utc(observedAtUtc, nameof(observedAtUtc));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-production-arm-ready-receipt-v1", AttemptId.ToString("D"), RuntimeEpoch.ToString("D"),
            AuthorizationEventHash, Activation?.Position.ToString(CultureInfo.InvariantCulture),
            Activation?.ActivationId.ToString("D"), Activation?.ContentHash, MaintenanceHeadHash,
            AdmissionGeneration.ToString(CultureInfo.InvariantCulture), ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            ConnectionGeneration.ToString(CultureInfo.InvariantCulture), ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }
    public Guid AttemptId { get; }
    public Guid RuntimeEpoch { get; }
    public string AuthorizationEventHash { get; }
    public RecipeActivationReference? Activation { get; }
    public string MaintenanceHeadHash { get; }
    public long AdmissionGeneration { get; }
    public uint ControllerEpoch { get; }
    public long ConnectionGeneration { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string ContentHash { get; }
}
