namespace Foundry.Stoke.Auth;

/// <summary>
/// Resolves the control-plane credential following the documented precedence
/// (US4, contracts/credential-provider.md, ADR 0005). Mirror of the Python
/// <c>CredentialProvider</c> public surface.
///
/// Resolution precedence:
/// <list type="number">
///   <item>An explicitly injected credential (SEC-004: deterministic prod).</item>
///   <item>The primary (Entra ID) credential produced by the factory, unless it
///   is unavailable.</item>
///   <item>API-key / connection-string fallback, if configured.</item>
///   <item>Otherwise <see cref="Errors.NoCredentialAvailableException"/> (CC-005).</item>
/// </list>
/// </summary>
public interface ICredentialProvider
{
    /// <summary>Return a usable credential following the resolution precedence.</summary>
    /// <exception cref="Errors.NoCredentialAvailableException">
    /// Raised when neither the primary path nor a configured fallback is
    /// available (CC-005).
    /// </exception>
    object ResolveCredential();
}
