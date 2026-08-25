// Typed errors for the Stoke control-plane library (mirror of the Python
// foundry_stoke.errors module). Expected failures are represented as typed
// exceptions so callers can branch on intent rather than parsing messages
// (coding-guidelines: explicit result-based error handling). Every error
// derives from StokeException. Names keep the cross-language neutral stem
// (NotFound, AlreadyExists, ...) and add the .NET "Exception" suffix (ADR 0004:
// recognizable across languages, adapted to each language's conventions).

namespace Foundry.Stoke.Errors;

/// <summary>Base class for every error raised by Stoke.</summary>
public abstract class StokeException : Exception
{
    protected StokeException()
    {
    }

    protected StokeException(string? message)
        : base(message)
    {
    }

    protected StokeException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

// --- Durable store errors (US2, contracts/durable-store-provider.md) ---

/// <summary>Base class for durable store failures.</summary>
public abstract class StoreException : StokeException
{
    protected StoreException()
    {
    }

    protected StoreException(string? message)
        : base(message)
    {
    }

    protected StoreException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised by create when (id, partitionKey) already exists.</summary>
public sealed class AlreadyExistsException : StoreException
{
    public AlreadyExistsException()
    {
    }

    public AlreadyExistsException(string? message)
        : base(message)
    {
    }

    public AlreadyExistsException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised by read/delete when the record does not exist.</summary>
public sealed class NotFoundException : StoreException
{
    public NotFoundException()
    {
    }

    public NotFoundException(string? message)
        : base(message)
    {
    }

    public NotFoundException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a write is attempted with a stale etag (CC-003).</summary>
public sealed class ConcurrencyConflictException : StoreException
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string? message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when an id/partition key is empty, oversized, or unsafe (SEC-001).</summary>
public sealed class InvalidRecordKeyException : StoreException
{
    public InvalidRecordKeyException()
    {
    }

    public InvalidRecordKeyException(string? message)
        : base(message)
    {
    }

    public InvalidRecordKeyException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a persisted record is unreadable, partial, or oversized (SEC-002).</summary>
public sealed class CorruptedRecordException : StoreException
{
    public CorruptedRecordException()
    {
    }

    public CorruptedRecordException(string? message)
        : base(message)
    {
    }

    public CorruptedRecordException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a record's type is not in the allowed discriminators (SEC-002).</summary>
public sealed class UnknownRecordTypeException : StoreException
{
    public UnknownRecordTypeException()
    {
    }

    public UnknownRecordTypeException(string? message)
        : base(message)
    {
    }

    public UnknownRecordTypeException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when the cross-process file lock cannot be acquired in time (SEC-006).</summary>
public sealed class LockTimeoutException : StoreException
{
    public LockTimeoutException()
    {
    }

    public LockTimeoutException(string? message)
        : base(message)
    {
    }

    public LockTimeoutException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

// --- Session lifecycle errors (US1, contracts/session-controller.md) ---

/// <summary>Base class for session lifecycle failures.</summary>
public abstract class SessionException : StokeException
{
    protected SessionException()
    {
    }

    protected SessionException(string? message)
        : base(message)
    {
    }

    protected SessionException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when the idle timeout is outside the 300..3600 second range (CC-002).</summary>
public sealed class InvalidIdleTimeoutException : SessionException
{
    public InvalidIdleTimeoutException()
    {
    }

    public InvalidIdleTimeoutException(string? message)
        : base(message)
    {
    }

    public InvalidIdleTimeoutException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised for any operation on a session that has been deleted (FR-005).</summary>
public sealed class SessionClosedException : SessionException
{
    public SessionClosedException()
    {
    }

    public SessionClosedException(string? message)
        : base(message)
    {
    }

    public SessionClosedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when the Foundry control plane is unavailable or times out.</summary>
public sealed class FoundryUnavailableException : SessionException
{
    public FoundryUnavailableException()
    {
    }

    public FoundryUnavailableException(string? message)
        : base(message)
    {
    }

    public FoundryUnavailableException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when creating a session but the agent has no published version.</summary>
public sealed class NoAgentVersionAvailableException : SessionException
{
    public NoAgentVersionAvailableException()
    {
    }

    public NoAgentVersionAvailableException(string? message)
        : base(message)
    {
    }

    public NoAgentVersionAvailableException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

// --- Authentication errors (US4, contracts/credential-provider.md) ---

/// <summary>Base class for authentication failures.</summary>
public abstract class AuthException : StokeException
{
    protected AuthException()
    {
    }

    protected AuthException(string? message)
        : base(message)
    {
    }

    protected AuthException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when neither the primary credential nor a fallback is available (CC-005).</summary>
public sealed class NoCredentialAvailableException : AuthException
{
    public NoCredentialAvailableException()
    {
    }

    public NoCredentialAvailableException(string? message)
        : base(message)
    {
    }

    public NoCredentialAvailableException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a credential is present but rejected by the service.</summary>
public sealed class AuthenticationFailedException : AuthException
{
    public AuthenticationFailedException()
    {
    }

    public AuthenticationFailedException(string? message)
        : base(message)
    {
    }

    public AuthenticationFailedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

// --- Configuration / endpoint errors (SEC-010, config facade) ---

/// <summary>Raised when required configuration is missing or invalid.</summary>
public class ConfigurationException : StokeException
{
    public ConfigurationException()
    {
    }

    public ConfigurationException(string? message)
        : base(message)
    {
    }

    public ConfigurationException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when an endpoint is not https or does not match the expected host (SEC-010).</summary>
public sealed class InvalidEndpointException : ConfigurationException
{
    public InvalidEndpointException()
    {
    }

    public InvalidEndpointException(string? message)
        : base(message)
    {
    }

    public InvalidEndpointException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

// --- Warm-up errors (US3, contracts/warmup-strategy.md) ---

/// <summary>Base class for warm-up failures.</summary>
public abstract class WarmupException : StokeException
{
    protected WarmupException()
    {
    }

    protected WarmupException(string? message)
        : base(message)
    {
    }

    protected WarmupException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a pool target size exceeds the configured maximum (SEC-007).</summary>
public sealed class TargetSizeExceededException : WarmupException
{
    public TargetSizeExceededException()
    {
    }

    public TargetSizeExceededException(string? message)
        : base(message)
    {
    }

    public TargetSizeExceededException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
