namespace Demo;

// IQueryHandler is a claimed handler interface, dispatched by Send.
public interface IQueryHandler<T> { }

public interface IMediator
{
    void Send<T>(T query);
}

public record GetOrder(int Id);

public class GetOrderHandler : IQueryHandler<GetOrder>
{
    public void Handle(GetOrder query) { }
}

public class Caller
{
    private readonly IMediator _mediator = null!;

    public void Go() => _mediator.Send(new GetOrder(1));
}
