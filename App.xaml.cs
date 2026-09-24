// Подключаем пространство имён с классами внедрения зависимостей (DI-контейнер)
using Microsoft.Extensions.DependencyInjection;

// Объявляем корневое пространство имён приложения
namespace MauiBluethooth;

// Объявление частичного класса приложения (наследует Application — точка входа MAUI-приложения)
public partial class App : Application
{
	// Конструктор приложения
	public App()
	{
		// Инициализирует компоненты, объявленные в XAML-файле App.xaml (ресурсы, стили)
		InitializeComponent();
	}

	// Метод создания главного окна приложения (вызывается фреймворком)
	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Создаём и возвращаем окно, содержимое которого — экземпляр AppShell
		return new Window(new AppShell());
	}
}