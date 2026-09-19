namespace Demo;

// INotificationHandler is one of the seven dispatch strings with no test. It is the fan-out form:
// one Publish reaches every handler for the message, so a dropped edge loses a handler silently.
public interface INotificationHandler<T> { }

public interface IMediator
{
    void Publish<T>(T notification);
}

public record OrderShipped(int Id);

public class EmailOnShipped : INotificationHandler<OrderShipped>
{
    public void Handle(OrderShipped notification) { }
}

public class AuditOnShipped : INotificationHandler<OrderShipped>
{
    public void Handle(OrderShipped notification) { }
}

public class Publisher
{
    private readonly IMediator _mediator = null!;

    public void Go(OrderShipped notification) => _mediator.Publish(notification);
}
