namespace Demo;

// Wolverine's Invoke, distinct from InvokeAsync, which is already covered by DispatchShapeTests.
public interface IRequestHandler<T> { }

public interface IMessageBus
{
    void Invoke<T>(T message);
}

public record Ping(int Id);

public class PingHandler : IRequestHandler<Ping>
{
    public void Handle(Ping message) { }
}

public class Caller
{
    private readonly IMessageBus _bus = null!;

    public void Go() => _bus.Invoke(new Ping(1));
}
