namespace Demo;

// ICommandHandler is a claimed handler interface, dispatched by Send.
public interface ICommandHandler<T> { }

public interface IMediator
{
    void Send<T>(T command);
}

public record CreateOrder(int Id);

public class CreateOrderHandler : ICommandHandler<CreateOrder>
{
    public void Handle(CreateOrder command) { }
}

public class Caller
{
    private readonly IMediator _mediator = null!;

    public void Go() => _mediator.Send(new CreateOrder(1));
}
