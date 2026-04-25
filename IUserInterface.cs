namespace AiInteraction;

/// <summary>
/// Abstraction for all user interactions. Decouples the AI logic from the hardcoded standard console.
/// </summary>
public interface IUserInterface
{
  void Write(string message);
  void WriteLine(string message = "");
  string? ReadLine();
}