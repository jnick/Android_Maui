// Условная компиляция: подключаем псевдоним PlatformCameraPreview — тип нативного элемента предпросмотра.
// Такой приём позволяет написать ОДИН класс handler'а для обеих платформ, не дублируя код

// Если собираем под Android — нативный элемент предпросмотра это PreviewView из CameraX
#if ANDROID

// Импортируем PreviewView под нейтральным именем PlatformCameraPreview
using PlatformCameraPreview = AndroidX.Camera.View.PreviewView;

// Если собираем под Windows — нативный элемент предпросмотра это MediaPlayerElement из WinUI
#elif WINDOWS

// Импортируем MediaPlayerElement под тем же нейтральным именем
using PlatformCameraPreview = Microsoft.UI.Xaml.Controls.MediaPlayerElement;
#endif

// Подключаем наш кроссплатформенный контрол CameraPreview (нужен как тип VirtualView в базовом классе)
using MauiBluethooth.Presentation.Camera.Views;

// Подключаем базовые классы handler'ов MAUI: ViewHandler, IPropertyMapper, CommandMapper
using Microsoft.Maui.Handlers;

// Объявляем пространство имён для представлений раздела «Камера»
namespace MauiBluethooth.Presentation.Camera.Views;

// Handler нашего контрола CameraPreview: «мост» между кроссплатформенным контролом MAUI
// и нативным элементом платформы. Именно он создаёт PreviewView / MediaPlayerElement.
//
// partial нужен потому, что сам CameraPreviewHandler на Android и Windows компилируется
// из разных кусков кода (директивы #if), а объявление класса общее
public partial class CameraPreviewHandler : ViewHandler<CameraPreview, PlatformCameraPreview>
{
	// Карта свойств: говорит handler'у, на какие свойства нашего контрола он должен реагировать.
	// Пока используем базовую ViewMapper — свойств, требующих синхронизации с нативным элементом,
	// у контрола пока нет (CameraService задаётся один раз до создания)
	public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> PropertyMapper =
		new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewMapper);

	// Карта команд: описывает, какие команды нашего контрола поддерживаются нативно.
	// Тоже базовая — своих команд у CameraPreview нет
	public static readonly CommandMapper<CameraPreview, CameraPreviewHandler> CommandMapper =
		new CommandMapper<CameraPreview, CameraPreviewHandler>(ViewCommandMapper);

	// Конструктор handler'а. MAUI вызывает его сам при создании контрола на экране
	public CameraPreviewHandler() : base(PropertyMapper, CommandMapper)
	{
		// Тело пустое: вся настройка уже передана в базовый конструктор
	}

	// Создание нативного элемента. Вызывается один раз, когда контрол появляется на экране
	protected override PlatformCameraPreview CreatePlatformView()
	{
		// Достаём камерный сервис из нашего контрола.
		// «??» с throw: если сервис не задан (забыли привязать в XAML), это ошибка разработчика —
		// падать сразу и с понятным сообщением лучше, чем потом получить null-ссылку
		var service = VirtualView.CameraService ?? throw new InvalidOperationException("Camera service is not configured");

		// Просим сервис создать нативный элемент предпросмотра:
		// Android → PreviewView, Windows → MediaPlayerElement (внутри CameraServiceBase
		// этот вызов делегируется платформенной реализации).
		// Тип возвращается как object, поэтому нужно привести к типу платформы
		return (PlatformCameraPreview)service.CreatePlatformPreview();
	}

	// Отключение handler'а: вызывается, когда контрол уходит с экрана (навигация прочь)
	protected override void DisconnectHandler(PlatformCameraPreview platformView)
	{
		// Сначала просим базовый класс выполнить стандартную очистку
		base.DisconnectHandler(platformView);

		// Останавливаем камеру. Это важно: нативный элемент скоро будет уничтожен,
		// а камера останется занятой нашим приложением, если её вовремя не освободить.
		// «_ =» — запускаем задачу без ожидания: DisconnectHandler не может быть асинхронным,
		// а сам StopAsync внутри защищён семафором и ошибок не пробрасывает
		_ = VirtualView.CameraService?.StopAsync();
	}
}
