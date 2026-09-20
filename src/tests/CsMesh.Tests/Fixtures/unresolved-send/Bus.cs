namespace Demo;

// The honest non-resolving shape: Send carries a request type with no handler anywhere. This must
// produce a 'mediatr/no-handler' row and no edge -- and it guards the opposite failure, a future
// change that invents an edge where none exists.
public interface IRequestHandler<T> { }

public interface IMediator
{
    void Send<T>(T request);
}

public record OrphanCommand(int Id);

public class Caller
{
    private readonly IMediator _mediator = null!;

    public void Go() => _mediator.Send(new OrphanCommand(1));
}
