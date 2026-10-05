using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }

    public enum ServiceLifetime { Singleton = 0, Scoped = 1, Transient = 2 }

    // The real extension class name and namespace. The stubs name the shapes the indexer reads;
    // their bodies are never run.
    public static class EntityFrameworkServiceCollectionExtensions
    {
        public static IServiceCollection AddDbContext<TContext>(
            this IServiceCollection services,
            System.Action<object>? optionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
            ServiceLifetime optionsLifetime = ServiceLifetime.Scoped) => services;

        public static IServiceCollection AddDbContextPool<TContext>(
            this IServiceCollection services,
            System.Action<object>? optionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped) => services;

        public static IServiceCollection AddDbContext<TContextService, TContextImplementation>(
            this IServiceCollection services,
            System.Action<object>? optionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
            ServiceLifetime optionsLifetime = ServiceLifetime.Scoped) => services;

        public static IServiceCollection AddDbContextPool<TContextService, TContextImplementation>(
            this IServiceCollection services,
            System.Action<object>? optionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped) => services;

        public static IServiceCollection AddDbContextFactory<TContext>(
            this IServiceCollection services,
            System.Action<object>? optionsAction = null,
            ServiceLifetime lifetime = ServiceLifetime.Singleton) => services;
    }
}

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext { }

    public interface IDbContextFactory<TContext> { TContext CreateDbContext(); }
}

namespace Demo
{
    public sealed class OrdersContext : DbContext { }
    public sealed class BillingContext : DbContext { }
    public sealed class AuditContext : DbContext { }
    public sealed class NegativeContext : DbContext { }

    public interface IOrdersStore { }
    public sealed class OrdersStore : IOrdersStore { }
    public interface IBillingStore { }
    public sealed class BillingStore : IBillingStore { }
    public interface IClock { }
    public sealed class SystemClock : IClock { }

    // A receiver that is not the container. The same method names must produce nothing here.
    public sealed class NotAContainer
    {
        public void AddDbContext<T>() { }
        public void AddDbContextFactory<T>() { }
    }

    public static class Wiring
    {
        public static void Register(IServiceCollection services)
        {
            // One generic argument: the context registers itself, scoped.
            services.AddDbContext<OrdersContext>();
            services.AddDbContextPool<BillingContext>();

            // Two generic arguments: the service binds to the implementation, scoped.
            services.AddDbContext<IOrdersStore, OrdersStore>();
            services.AddDbContextPool<IBillingStore, BillingStore>();

            // A constant contextLifetime overrides the scoped default.
            services.AddDbContext<IClock, SystemClock>(contextLifetime: ServiceLifetime.Transient);

            // The factory registers IDbContextFactory<T>, singleton, not the context.
            services.AddDbContextFactory<AuditContext>();

            // Same method names on a receiver that is not the container. NegativeContext is used
            // nowhere else, so any tag or edge it gains here would be the false registration.
            var notAContainer = new NotAContainer();
            notAContainer.AddDbContext<NegativeContext>();
            notAContainer.AddDbContextFactory<NegativeContext>();
        }
    }
}
