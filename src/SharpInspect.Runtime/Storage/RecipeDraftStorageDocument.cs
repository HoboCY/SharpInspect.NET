using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Canonical draft payload produced by the Recipe Draft codec. Storage assigns
/// revision, position, author and timestamp; those values deliberately do not
/// appear in this transport document.
/// </summary>
internal sealed record RecipeDraftDocument(RecipeDraftContent Content, string PayloadJson, string PayloadHash);
