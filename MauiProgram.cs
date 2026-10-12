#if ANDROID
// Подключаем Android-реализацию Bluetooth-сервиса (используется под #if ANDROID)
using MauiBluethooth.Platforms.Android.Services;
#endif
#if WINDOWS
// Подключаем Android-реализацию Bluetooth-сервиса (используется под #if ANDROID)
using MauiBluethooth.Platforms.Windows.Services;
#endif
// Подключаем ViewModel экрана камеры для регистрации в DI
using MauiBluethooth.Presentation.Camera.ViewModels;
// Подключаем страницу и контролы экрана камеры для регистрации в DI и handlers
using MauiBluethooth.Presentation.Camera.Views;
// Подключаем ViewModel экрана чата для регистрации в DI
using MauiBluethooth.Presentation.Chat.ViewModels;
// Подключаем страницу экрана чата для регистрации в DI
using MauiBluethooth.Presentation.Chat.Views;
// Подключаем страницы раздела «Распознавание» для регистрации в DI
using MauiBluethooth.Presentation.Detection.Views;
// Подключаем ViewModel экрана устройств для регистрации в DI
using MauiBluethooth.Presentation.Devices.ViewModels;
// Подключаем страницу экрана устройств для регистрации в DI
using MauiBluethooth.Presentation.Devices.Views;
// Подключаем ViewModel главного экрана
using MauiBluethooth.Presentation.Main.ViewModels;
// Подключаем главную страницу
using MauiBluethooth.Presentation.Main.Views;
// Подключаем сервисы распознавания: каталог моделей, детектор и передатчик по Bluetooth
using MauiBluethooth.Services.Detection;
// Подключаем передатчик результатов распознавания по Bluetooth
using MauiBluethooth.Services.Detection.Transmission;
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
			})
			// Регистрируем кастомные handler'ы — так MAUI узнаёт, какой нативный элемент
			// использовать для наших кроссплатформенных контролов
			.ConfigureMauiHandlers(handlers =>
			{
				// Handler камеры реализован только для Android и Windows.
				// На других платформах (iOS) нативного элемента в проекте нет,
				// и без директивы условной компиляции сборка iOS упала бы на необъявленном типе
#if ANDROID || WINDOWS

				// Связываем наш контрол CameraPreview с нативной реализацией:
				// PreviewView (CameraX) на Android, MediaPlayerElement на Windows
				handlers.AddHandler(typeof(CameraPreview), typeof(CameraPreviewHandler));
#endif
			});

		// Регистрируем главную ViewModel как singleton (один экземпляр на всё приложение)
		builder.Services.AddSingleton<MainViewModel>();
		// Регистрируем главную страницу как singleton
		builder.Services.AddSingleton<MainPage>();

		// Регистрируем ViewModel экрана устройств как singleton
		builder.Services.AddSingleton<DevicesViewModel>();
		// Регистрируем страницу устройств как singleton
		builder.Services.AddSingleton<DevicesPage>();
		// Регистрируем ViewModel экрана камеры как singleton
		builder.Services.AddSingleton<CameraViewModel>();

		// Страницы диагностики и заданий — singleton.
		// Как и остальные страницы: у них нет собственного состояния,
		// а данные они читают из CameraViewModel, который и так единственный.
		// Регистрация нужна Shell для разрешения типа при переходе по маршруту
		builder.Services.AddSingleton<DiagnosticsPage>();
		builder.Services.AddSingleton<TasksPage>();

		// Каталог моделей распознавания — singleton.
		// Именно singleton, потому что каталог хранит пути к уже распакованным файлам:
		// при новом экземпляре каждый файл распаковывался бы заново,
		// а это десятки мегабайт лишнего ввода-вывода при каждом переключении модели
		builder.Services.AddSingleton<ModelRegistry>();

		// Детектор — singleton по той же причине: он держит в памяти сеть OpenCV
		// весом в десятки мегабайт. Новый экземпляр на каждый кадр или на каждую страницу
		// означал бы повторную загрузку модели
		builder.Services.AddSingleton<ObjectDetector>();

		// Передатчик распознанных объектов по Bluetooth — singleton.
		// Он подписывается на снимки детектора и живёт всё приложение:
		// отдельный экземпляр на страницу создал бы несколько подписок
		builder.Services.AddSingleton<DetectionTransmitter>();

		// Регистрируем страницу камеры как singleton.
		// Именно singleton, а не transient: страница держит нативный PreviewView, и при каждом
		// создании нового экземпляра камера открывалась бы заново (или, наоборот, не закрывалась бы)
		builder.Services.AddSingleton<CameraPage>();

		// Регистрируем ViewModel экрана чата как singleton
		builder.Services.AddSingleton<ChatViewModel>();
		// Регистрируем страницу чата как singleton
		builder.Services.AddSingleton<ChatPage>();

		// Директивы условной компиляции: сборка только для Android
#if ANDROID
		// На Android используем Android-реализацию Bluetooth-сервиса
		builder.Services.AddSingleton<IBluetoothService, AndroidBluetoothService>();

		// На Android камеру обслуживает CameraX. Интерфейс и реализация — единственный экземпляр:
		// App, CameraPage и ViewModel должны работать с одним и тем же сервисом,
		// иначе они не видели бы состояние друг друга
		builder.Services.AddSingleton<ICameraService, AndroidCameraService>();
		// Иначе если сборка для Windows
#elif WINDOWS
		// На Windows используем Windows-реализацию Bluetooth-сервиса
		builder.Services.AddSingleton<IBluetoothService, WindowsBluetoothService>();

		// На Windows камеру обслуживает MediaCapture. Требования к регистрации те же: один экземпляр на приложение
		builder.Services.AddSingleton<ICameraService, WindowsCameraService>();
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