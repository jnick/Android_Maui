// Подключаем пространство имён WinUI (Microsoft.UI.Xaml) для использования InitializeComponent
using Microsoft.UI.Xaml;

// Текстовые комментарии шаблона проекта (можно прочитать по ссылке в конце)
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

// Пространство имён Windows-версии приложения (добавляется суффикс .WinUI)
namespace MauiBluethooth.WinUI;

/// <summary>
/// Предоставляет поведение, специфичное для приложения, дополняя базовый класс Application.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Инициализирует объект-одиночку приложения. Это первая строка исполняемого кода,
	/// и как таковое — логический эквивалент метода main() или WinMain().
	/// </summary>
	public App()
	{
		// Вызываем инициализацию компонентов WinUI (App.xaml)
		this.InitializeComponent();
	}

	// Переопределяем метод создания MAUI-приложения, вызывая общую точку сборки
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
	// (реализация — вызов MauiProgram.CreateMauiApp, где настроен весь DI)
}