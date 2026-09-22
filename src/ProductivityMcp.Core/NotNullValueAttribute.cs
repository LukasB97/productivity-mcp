using System.ComponentModel.DataAnnotations;

namespace ProductivityMcp.Core;

// Unlike Required, this does not mark a defaulted property as required in the
// generated JSON schema. Omission keeps its default; explicit null is invalid.
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotNullValueAttribute() : ValidationAttribute("{0} must not be null.")
{
    public override bool IsValid(object? value) => value is not null;
}
