using FluentValidation;

namespace HandyNet.SharedKernel.Web;

public static class ValidationRuleExtensions
{
    /// <summary>
    /// Limits a collection to <paramref name="max"/> entries. Reports the stable code <c>validation.maxItems</c> with a
    /// <c>max</c> parameter so the UI can show the limit in its own language; a null collection is left to NotNull/NotEmpty.
    /// </summary>
    public static IRuleBuilderOptions<T, TCollection> MaxCount<T, TCollection>(
        this IRuleBuilder<T, TCollection> rule, Func<TCollection, int> count, int max, string message) =>
        rule.Must((_, value, context) =>
            {
                context.MessageFormatter.AppendArgument("Max", max);
                return value is null || count(value) <= max;
            })
            .WithMessage(message)
            .WithErrorCode("validation.maxItems");
}
