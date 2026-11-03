using System.Text;
using CS2OpenDev.ConfigManager.Archive;

// The key ceremony for the identity level (docs/identity-service.md, "Keys"). Every command works on files the
// holder keeps offline; nothing here talks to a network. The commands are in Ceremony.
return new Ceremony(Console.Out, Console.Error, ReadHidden, ReadShown).Run(args);

// A line typed at the console; null when input is not a console.
static string? ReadShown(string prompt)
{
    if (Console.IsInputRedirected)
    {
        return null;
    }

    Console.Error.Write(prompt);
    return Console.ReadLine();
}

// A passphrase typed at the console without echo; null when input is not a console or nothing is typed.
static string? ReadHidden(string prompt)
{
    if (Console.IsInputRedirected)
    {
        return null;
    }

    Console.Error.Write(prompt);
    StringBuilder text = new();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            text.Clear();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            text.Length = Math.Max(0, text.Length - 1);
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }

    Console.Error.WriteLine();
    return text.Length == 0 ? null : text.ToString();
}
