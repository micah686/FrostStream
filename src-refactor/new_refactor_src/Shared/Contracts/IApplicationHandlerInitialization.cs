namespace FrostStream.ApplicationContracts;

public interface IApplicationHandlerInitialization
{
    Task RegistrationCompleted { get; }
}
