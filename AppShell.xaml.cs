// Подключаем пространство имён с представлениями раздела «Устройства» (для регистрации маршрута)
using MauiBluethooth.Presentation.Devices.Views;

// Объявляем корневое пространство имён приложения
namespace MauiBluethooth;

// Объявление класс оболочки приложения (Shell — навигационный каркас с нижним меню)
public partial class AppShell : Shell
{
	// Конструктор оболочки — вызывается при создании приложения
	public AppShell()
	{
		// Инициализирует компоненты, объявленные в XAML-файле AppShell.xaml
		InitializeComponent();
		// Регистрируем маршрут «devices» — по нему Shell открывает страницу DevicesPage (GoToAsync("devices"))
		Routing.RegisterRoute("devices", typeof(DevicesPage));
	}
}