using System;

namespace AiInteraction;

/// <summary>
/// Standard implementation of the IUserInterface using the Windows System Console.
/// </summary>
public class ConsoleUserInterface : IUserInterface
{
  public void Write(string message) => Console.Write(message);
  public void WriteLine(string message = "") => Console.WriteLine(message);
  public string? ReadLine() => Console.ReadLine();
}