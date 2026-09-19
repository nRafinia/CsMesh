namespace Demo;

// The positive SendAsync path: not just that a transport call is rejected, but that a real one links.
public interface IRequestHandler<T> { }

public interface IMediator
{
    Task SendAsync<T>(T request);
}

public record ShipOrder(int Id);

public class ShipOrderHandler : IRequestHandler<ShipOrder>
{
    public Task Handle(ShipOrder request) => Task.CompletedTask;
}

public class Shipper
{
    private readonly IMediator _mediator = null!;

    public Task Go() => _mediator.SendAsync(new ShipOrder(1));
}
