using ObservabilityAssistant.Core.AI;
using Spectre.Console;
namespace ObservabilityAssistant.Console.UI;
public class ChatInterface
{
    private readonly AIOrchestrator _orchestrator;
    public ChatInterface(AIOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }
    public async Task RunAsync()
    {
        ShowWelcomeBanner();
        while (true)
        {
            var userInput = AnsiConsole.Ask<string>("[cyan]You:[/]");
            if (userInput.Trim().ToLower() == "exit" || userInput.Trim().ToLower() == "quit")
            {
                AnsiConsole.MarkupLine("[yellow]Goodbye! 👋[/]");
                break;
            }
            if (userInput.Trim().ToLower() == "clear")
            {
                AnsiConsole.Clear();
                ShowWelcomeBanner();
                continue;
            }
            if (userInput.Trim().ToLower() == "reset")
            {
                _orchestrator.ResetConversation();
                AnsiConsole.MarkupLine("[green]✓ Conversation reset[/]");
                continue;
            }
            if (userInput.Trim().ToLower() == "help")
            {
                ShowHelp();
                continue;
            }
            if (string.IsNullOrWhiteSpace(userInput))
            {
                continue;
            }
            var response = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync("[green]Thinking...[/]", async ctx =>
                {
                    return await _orchestrator.ProcessUserMessageAsync(userInput);
                });
            DisplayResponse(response);
        }
    }
    private void ShowWelcomeBanner()
    {
        var rule = new Rule("[bold cyan]🤖 Observability Assistant[/]")
        {
            Justification = Justify.Left
        };
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine("[dim]Your AI-powered Kubernetes and observability companion[/]");
        AnsiConsole.MarkupLine("[dim]Type 'help' for commands, 'exit' to quit[/]");
        AnsiConsole.WriteLine();
    }
    private void ShowHelp()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Command[/]")
            .AddColumn("[bold]Description[/]");
        table.AddRow("[cyan]exit, quit[/]", "Exit the application");
        table.AddRow("[cyan]clear[/]", "Clear the screen");
        table.AddRow("[cyan]reset[/]", "Reset the conversation history");
        table.AddRow("[cyan]help[/]", "Show this help message");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        var examplesPanel = new Panel(@"[bold]Example queries:[/]
• ¿Cuántos pods hay en el namespace apps?
• Muéstrame los logs del pod demo-api
• Lista todos los namespaces
• ¿Hay algún pod con problemas?
• Escala el deployment demo-api a 3 réplicas")
        {
            Header = new PanelHeader("[yellow]💡 Try these commands[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow)
        };
        AnsiConsole.Write(examplesPanel);
        AnsiConsole.WriteLine();
    }
    private void DisplayResponse(string response)
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(new Markup($"[white]{Markup.Escape(response)}[/]"))
        {
            Header = new PanelHeader("[bold green]🤖 Assistant[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Padding = new Padding(2, 1)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
}
