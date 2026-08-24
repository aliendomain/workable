using System.Security.Claims;

namespace Workable;

/// <summary>
/// Creates a Workable actor from an authenticated identity using integration-specific claim semantics.
/// </summary>
public interface IWorkActorClaimsMapper
{
    /// <summary>
    /// Gets the mapper order. Host mappers run before integration defaults when they use a lower value.
    /// </summary>
    int Order => 0;

    /// <summary>
    /// Attempts to create an actor from the selected authenticated identity.
    /// </summary>
    /// <param name="identity">The identity selected for Workable.</param>
    /// <param name="actor">The mapped actor when this mapper owns the identity.</param>
    /// <returns><see langword="true"/> when this mapper owns the identity; otherwise <see langword="false"/>.</returns>
    bool TryCreate(ClaimsIdentity identity, out WorkActor actor);
}
