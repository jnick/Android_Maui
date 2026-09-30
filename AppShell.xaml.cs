// Подключаем пространство имён с представлениями раздела «Устройства» (для регистрации маршрута)
using MauiBluethooth.Presentation.Devices.Views;
// Подключаем пространство имён с представлениями раздела «Камера» (для регистрации маршрута)
using MauiBluethooth.Presentation.Camera.Views;
// Подключаем пространство имён с представлениями раздела «Чат» (для регистрации маршрута)
using MauiBluethooth.Presentation.Chat.Views;

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
		// Регистрируем маршрут «camera» — по нему Shell открывает страницу CameraPage (GoToAsync("camera")).
		// Регистрация обязательна: без неё Shell не знает, какую страницу показать по этому имени
		Routing.RegisterRoute("camera", typeof(CameraPage));
		// Регистрируем маршрут «chat» — по нему Shell открывает страницу ChatPage (GoToAsync("chat"))
		Routing.RegisterRoute("chat", typeof(ChatPage));
	}
}