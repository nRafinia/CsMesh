namespace Demo;

// MassTransit's IHandleMessages<T>, with Handle as the entry method.
public interface IHandleMessages<T> { }

public interface IBus
{
    void Publish<T>(T message);
}

public record OrderPlaced(int Id);

public class OrderPlacedConsumer : IHandleMessages<OrderPlaced>
{
    public void Handle(OrderPlaced message) { }
}

public class Publisher
{
    private readonly IBus _bus = null!;

    public void Go(OrderPlaced message) => _bus.Publish(message);
}
