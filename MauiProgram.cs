#if ANDROID
// Подключаем Android-реализацию Bluetooth-сервиса (используется под #if ANDROID)
using MauiBluethooth.Platforms.Android.Services;
#endif
#if WINDOWS
// Подключаем Android-реализацию Bluetooth-сервиса (используется под #if ANDROID)
using MauiBluethooth.Platforms.Windows.Services;
#endif
// Подключаем ViewModel экрана устройств для регистрации в DI
using MauiBluethooth.Presentation.Devices.ViewModels;
// Подключаем страницу экрана устройств для регистрации в DI
using MauiBluethooth.Presentation.Devices.Views;
// Подключаем ViewModel главного экрана
using MauiBluethooth.Presentation.Main.ViewModels;
// Подключаем главную страницу
using MauiBluethooth.Presentation.Main.Views;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;
// Подключаем логирование Microsoft
using Microsoft.Extensions.Logging;

// Объявляем корневое пространство имён приложения
namespace MauiBluethooth;

// Статический класс конфигурации приложения (эквивалент Program.cs)
public static class MauiProgram
{
	// Точка сборки MAUI-приложения — возвращает сконфигурированный MauiApp
	public static MauiApp CreateMauiApp()
	{
		// Создаём билдер приложения
		var builder = MauiApp.CreateBuilder();
		// Конфигурируем приложение и шрифты
		builder
			// Указываем главный класс приложения (App)
			.UseMauiApp<App>()
			// Настраиваем шрифты
			.ConfigureFonts(fonts =>
			{
				// Регистрируем обычный шрифт OpenSans под именем OpenSansRegular
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				// Регистрируем полужирный шрифт OpenSans под именем OpenSansSemibold
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Регистрируем главную ViewModel как singleton (один экземпляр на всё приложение)
		builder.Services.AddSingleton<MainViewModel>();
		// Регистрируем главную страницу как singleton
		builder.Services.AddSingleton<MainPage>();

		// Регистрируем ViewModel экрана устройств как singleton
		builder.Services.AddSingleton<DevicesViewModel>();
		// Регистрируем страницу устройств как singleton
		builder.Services.AddSingleton<DevicesPage>();

		// Директивы условной компиляции: сборка только для Android
#if ANDROID
		// На Android используем Android-реализацию Bluetooth-сервиса
		builder.Services.AddSingleton<IBluetoothService, AndroidBluetoothService>();
		// Иначе если сборка для Windows
#elif WINDOWS
		// На Windows используем Windows-реализацию Bluetooth-сервиса
		builder.Services.AddSingleton<IBluetoothService, WindowsBluetoothService>();
		// Конец условной компиляции
#endif

		// Директива условной компиляции: логирование только в DEBUG-сборках
#if DEBUG
		// Добавляем вывод логов в отладчик
		builder.Logging.AddDebug();
		// Конец условной компиляции
#endif

		// Собираем и возвращаем готовое приложение
		return builder.Build();
	}
}