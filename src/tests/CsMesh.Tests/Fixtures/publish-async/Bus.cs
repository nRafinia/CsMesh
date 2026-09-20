namespace Demo;

// PublishAsync as a notification fan-out verb.
public interface INotificationHandler<T> { }

public interface IMediator
{
    Task PublishAsync<T>(T notification);
}

public record OrderShipped(int Id);

public class Notifier : INotificationHandler<OrderShipped>
{
    public Task Handle(OrderShipped notification) => Task.CompletedTask;
}

public class Publisher
{
    private readonly IMediator _mediator = null!;

    public Task Go(OrderShipped notification) => _mediator.PublishAsync(notification);
}
