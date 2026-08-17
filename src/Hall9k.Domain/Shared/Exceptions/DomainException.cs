namespace Hall9k.Domain.Shared.Exceptions;

public abstract class DomainException(string message) : Exception(message);

/// <summary>Input fails the domain's validation rules (CLI: usage error exit code).</summary>
public sealed class DomainValidationException(string message) : DomainException(message);

/// <summary>The operation conflicts with current state (e.g. duplicate registration).</summary>
public sealed class DomainConflictException(string message) : DomainException(message);

/// <summary>The referenced aggregate does not exist.</summary>
public sealed class DomainNotFoundException(string message) : DomainException(message);

/// <summary>The operation violates a business rule despite valid input.</summary>
public sealed class DomainBusinessRuleException(string message) : DomainException(message);
