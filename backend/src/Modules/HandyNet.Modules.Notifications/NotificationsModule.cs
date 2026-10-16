using HandyNet.Modules.Notifications.Application;
using HandyNet.Modules.Notifications.Infrastructure;
using HandyNet.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Notifications;

/// <summary>
/// Turns integration events from the outbox into emails. Has no endpoints yet; it only consumes
/// events, so no other module depends on it.
/// </summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationOptions>().Bind(configuration.GetSection(NotificationOptions.Section));
        services.AddSingleton<IModuleModel, NotificationsModel>();
        services.AddScoped<EmailQueue>();
        services.AddScoped<IOutboxHandler, OrderPlacedHandler>();
        services.AddScoped<IOutboxHandler, OrderStatusChangedHandler>();
        services.AddScoped<IOutboxHandler, OrganizationVerifiedHandler>();
        services.AddScoped<IOutboxHandler, EmailVerificationRequestedHandler>();
        services.AddScoped<IOutboxHandler, PasswordResetRequestedHandler>();

        if (string.IsNullOrWhiteSpace(configuration[$"{NotificationOptions.Section}:Smtp:Host"]))
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddSingleton<EmailDeliveryWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailDeliveryWorker>());
        return services;
    }
}
