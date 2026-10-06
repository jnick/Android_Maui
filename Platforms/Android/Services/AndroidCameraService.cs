// Подключаем пространство имён Android с классом Application (глобальный контекст приложения)
using Android.App;
// Подключаем пространство имён Android с классом Context
using Android.Content;
// Подключаем AndroidX Camera Core: IImageProxy (кадр), ImageAnalysis и Preview (use case'ы камеры),
// CameraSelector (выбор камеры) и ProcessCameraProvider (точка доступа ко всем камерам)
using AndroidX.Camera.Core;
// Подключаем AndroidX Camera Lifecycle (нужно для привязки камеры к жизненному циклу Activity)
using AndroidX.Camera.Lifecycle;
// Подключаем AndroidX Camera View с классом PreviewView — нативный элемент предпросмотра
using AndroidX.Camera.View;
// Подключаем Java.Util.Concurrent с IExecutor — интерфейс исполнителя задач из AndroidX (SetAnalyzer требует его)
using Java.Util.Concurrent;
// Подключаем наши модели данных (CameraOption, CameraFrame, CameraFrameGeometry, CameraStartResult)
using MauiBluethooth.Models.Camera;
// Подключаем базовый камерный сервис и его интерфейс
using MauiBluethooth.Services;
// Подключаем API разрешений MAUI (Permissions.Camera, PermissionStatus)
using Microsoft.Maui.ApplicationModel;

// Объявляем пространство имён Android-реализаций сервисов
namespace MauiBluethooth.Platforms.Android.Services;

// Реализация камерного сервиса на Android поверх CameraX (AndroidX Camera).
// Вся общая логика (разрешения, семафор, пожелание «камера должна работать») живёт в CameraServiceBase,
// здесь только специфика CameraX: получение провайдера, сборка use case'ов и разбор пикселей кадра
public sealed class AndroidCameraService : CameraServiceBase
{
	// Контекст приложения Android. Нужен для получения ProcessCameraProvider и создания PreviewView.
	// Взят глобальный Application.Context, а не Activity, потому что сервис — singleton
	private readonly Context _context;

	// Отдельный однопоточный исполнитель для анализа кадров.
	// Зачем не UI-поток: конвертация пикселей в byte[] на кадре 1080p занимает единицы миллисекунд,
	// и на UI-потоке это привело бы к «дёрганью» интерфейса. Один поток гарантирует, что кадры
	// обрабатываются последовательно и не будут обрабатываться одновременно
	private readonly IExecutor _executor = Executors.NewSingleThreadExecutor();

	// Нативный элемент предпросмотра (то, что показывает картинку). Создаётся лениво, при первом обращении
	private PreviewView? _previewView;

	// Провайдер камер CameraX. Через него проверяем наличие камер и привязываем use case'ы
	private ProcessCameraProvider? _cameraProvider;

	// Use case отображения: связывает поток камеры с SurfaceProvider нашего PreviewView
	private Preview? _previewUseCase;

	// Use case анализа: выдаёт нам сырые кадры для обработки (оверлей, распознавание объектов)
	private ImageAnalysis? _imageAnalysis;

	// Обёртка над колбэком анализа. Нужна, потому что CameraX ожидает Java-объект, а не C#-делегат
	private CameraAnalyzer? _analyzer;

	// Конструктор: DI вызывает его сам, руками создавать сервис не нужно
	public AndroidCameraService()
	{
		// Запоминаем глобальный контекст приложения
		_context = global::Android.App.Application.Context;
	}

	// Создаёт нативный элемент предпросмотра
	protected override object CreatePlatformPreviewCore()
	{
		// Создаём PreviewView только один раз: повторное создание отвязало бы Surface от камеры
		// и привело бы к чёрному экрану при навигации туда-сюда
		_previewView ??= new PreviewView(_context);

		// Возвращаем его как object — тип платформенный, в общем коде упоминать его нельзя
		return _previewView;
	}

	// Одноразовая инициализация: получаем провайдер камер и составляем список доступных камер
	protected override async Task InitializePlatformAsync()
	{
		// GetInstance всегда возвращает ListenableFuture, а не сам провайдер — так устроен CameraX
		var future = ProcessCameraProvider.GetInstance(_context);

		// Разворачиваем future. Task.Run нужен, чтобы не блокировать вызывающий поток на future.Get()
		// (метод блокирующий — вызовет deadlock, если его выполнить в UI-потоке)
		_cameraProvider = (ProcessCameraProvider?)await Task.Run(() => future.Get());

		// Провайдер не пришёл — продолжать бессмысленно
		if (_cameraProvider is null)
		{
			throw new InvalidOperationException("CameraX provider is unavailable");
		}

		// Собираем список камер: на Android нас интересуют только задняя и фронтальная
		var options = new List<CameraOption>();

		// Проверяем наличие задней камеры (умеет снимать фото — этого достаточно для видеопотока)
		if (_cameraProvider.HasCamera(CameraSelector.DefaultBackCamera))
		{
			// Идентификатор «rear» — короткий и понятный, платформа-зависимый формат здесь не нужен
			options.Add(new CameraOption("rear", "Задняя камера", CameraLensFacing.Rear));
		}

		// Проверяем наличие фронтальной камеры
		if (_cameraProvider.HasCamera(CameraSelector.DefaultFrontCamera))
		{
			options.Add(new CameraOption("front", "Фронтальная камера", CameraLensFacing.Front));
		}

		// Передаём список в базовый класс: он сам выберет камеру по умолчанию (заднюю)
		SetCameraOptions(options);
	}

	// Запуск камеры: собираем use case'ы и привязываем их к жизненному циклу Activity
	protected override Task StartPlatformAsync()
	{
		// Без провайдера или PreviewView запускать нечего. Теоретически сюда не попадём,
		// т.к. базовый класс сначала вызывает инициализацию, а превью создаётся раньше старта
		if (_cameraProvider is null || _previewView is null)
		{
			throw new InvalidOperationException("CameraX preview is not initialized");
		}

		// Преобразуем выбранный идентификатор в селектор CameraX
		var selector = SelectedCameraId == "front"
			? CameraSelector.DefaultFrontCamera
			: CameraSelector.DefaultBackCamera;

		// Камера могла исчезнуть между инициализацией и стартом (например, приложение другое её заняло)
		if (!_cameraProvider.HasCamera(selector))
		{
			throw new InvalidOperationException("Selected camera is unavailable");
		}

		// Отвязываем всё, что было привязано раньше.
		// Критично: у ProcessCameraProvider одновременно может быть только одна привязка,
		// поэтому без UnbindAll повторный старт (например, после возврата из фона) упадёт
		_cameraProvider.UnbindAll();

		// Создаём use case отображения картинки
		_previewUseCase = new Preview.Builder().Build();

		// Отдаём PreviewView его SurfaceProvider: теперь в PreviewView побегут кадры
		_previewUseCase.SurfaceProvider = _previewView.SurfaceProvider;

		// Создаём use case анализа изображений — источник наших сырых кадров
		_imageAnalysis = new ImageAnalysis.Builder()

			// StrategyKeepOnlyLatest: если обработка не успевает за камерами, выбрасываются старые кадры.
			// Так память не растёт, а задержка не накапливается (для видео в реальном времени это правильный выбор)
			.SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)

			// Просим RGBA_8888: порядок байт R,G,B,A совпадает с нашим CameraPixelFormat.Rgba,
			// и это избавляет от перестановки каналов при копировании пикселей
			.SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888)

			// Завершаем построение use case
			.Build();

		// Оборачиваем наш обработчик в Java-совместимый объект, понятный CameraX
		_analyzer = new CameraAnalyzer(ProcessFrame);
		System.Diagnostics.Debug.WriteLine($"[AndroidCameraService] Анализатор создан: {_analyzer.GetType().FullName}");

		// Указываем, каким потоком и каким анализатором обрабатывать кадры
		_imageAnalysis.SetAnalyzer(_executor, _analyzer);
		System.Diagnostics.Debug.WriteLine("[AndroidCameraService] Анализатор назначен ImageAnalysis");

		// Получаем текущую Activity как владельца жизненного цикла.
		// CameraX сам остановит камеру, когда Activity уйдёт в фон, — это поведение нам на руку
		var activity = Platform.CurrentActivity as global::AndroidX.Lifecycle.ILifecycleOwner
			?? throw new InvalidOperationException("Current activity is unavailable");

		// Привязываем use case'ы к Activity и выбранной камере: с этого момента идёт видео и кадры
		_cameraProvider.BindToLifecycle(activity, selector, _previewUseCase, _imageAnalysis);
		System.Diagnostics.Debug.WriteLine("[AndroidCameraService] Use case'ы привязаны к жизненному циклу");

		// Метод синхронный (реальной асинхронности нет) — возвращаем уже завершённую задачу
		return Task.CompletedTask;
	}

	// Остановка камеры и освобождение ресурсов CameraX
	protected override Task StopPlatformAsync()
	{
		// Отвязываем все use case'ы от жизненного цикла: камера освобождается от других приложений
		_cameraProvider?.UnbindAll();

		// Убираем анализатор: кадры больше не приходят, и следующий Analyze не будет вызван
		_imageAnalysis?.ClearAnalyzer();

		// Освобождаем Java-обёртку анализатора и обнуляем ссылку (GC сможет собрать объект)
		_analyzer?.Dispose();
		_analyzer = null;

		// Освобождаем use case анализа
		_imageAnalysis?.Dispose();
		_imageAnalysis = null;

		// Освобождаем use case отображения
		_previewUseCase?.Dispose();
		_previewUseCase = null;

		// Синхронный метод — возвращаем завершённую задачу
		return Task.CompletedTask;
	}

	// Обработка одного кадра: копирование пикселей в управляемый массив и отправка подписчикам.
	// Метод вызывается из CameraAnalyzer на фоновом потоке _executor, а не в UI-потоке
	private void ProcessFrame(IImageProxy image)
	{
		try
		{
			// Получаем плоскости (plane) изображения. В RGBA_8888 обычно одна плоскость со всеми каналами
			var planes = image.GetPlanes();

			// Если плоскостей нет — это «пустой» кадр, обрабатывать нечего
			if (planes is null || planes.Length == 0)
			{
				return;
			}

			// Берём первую (и единственную нужную) плоскость
			var plane = planes[0];

			// Размеры кадра из метаданных изображения
			var width = image.Width;
			var height = image.Height;

			// RowStride: сколько байт реально занимает одна строка в буфере.
			// Часто БОЛЬШЕ, чем width * 4, — драйвер выравнивает строки по границе
			var rowStride = plane.RowStride;

			// PixelStride: сколько байт отделяет соседние пиксели. Равен 4 для RGBA_8888,
			// но может быть больше, если канал хранится отдельно
			var pixelStride = plane.PixelStride;

			// Готовим массив под плотно упакованный кадр: 4 байта на пиксель, без выравнивания
			var data = new byte[width * height * 4];

			// Получаем прямую буферную память из кадра (NativeBuffer)
			var buffer = plane.Buffer;

			// Возвращаем позицию чтения в начало. Кадр — одноразовый объект, буфер мог быть уже прочитан
			buffer.Rewind();

			// БЫСТРЫЙ ПУТЬ: данные уже лежат плотно, без выравнивания и без «дыр» между пикселями.
			// Это типичный случай для RGBA_8888, и он копирует кадр одним вызовом
			if (pixelStride == 4 && rowStride == width * 4)
			{
				buffer.Get(data);
			}
			else
			{
				// ОБЩИЙ ПУТЬ: пиксели разложены по строкам, а в строке между пикселями может быть
				// «выравнивание» (padding). Нужно перепаковывать построчно
				var row = new byte[rowStride];

				// Идём по строкам кадра сверху вниз
				for (var y = 0; y < height; y++)
				{
					// Читаем всю строку целиком в промежуточный буфер
					buffer.Get(row);

					// Смещение начала этой строки в нашем плотном массиве
					var rowOffset = y * width * 4;

					// Идём по пикселям строки слева направо
					for (var x = 0; x < width; x++)
					{
						// Где начинаются каналы текущего пикселя в исходной строке
						var sourceOffset = x * pixelStride;

						// Где должен начинаться этот пиксель в нашем плотном массиве
						var destinationOffset = rowOffset + x * 4;

						// Копируем 4 канала: R, G, B, A. Порядок менять не нужно — просили RGBA
						data[destinationOffset] = row[sourceOffset];
						data[destinationOffset + 1] = row[sourceOffset + 1];
						data[destinationOffset + 2] = row[sourceOffset + 2];
						data[destinationOffset + 3] = row[sourceOffset + 3];
					}
				}
			}

			// Угол поворота кадра: CameraX сообщает, на сколько градусов нужно повернуть изображение
			// (0/90/180/270), чтобы получить «правильную» ориентацию. «?.» и «?? 0» — на случай отсутствия метаданных
			var rotation = image.ImageInfo?.RotationDegrees ?? 0;

			// Собираем геометрию кадра. Зеркальность определяется тем, что выбрана фронтальная камера
			var geometry = new CameraFrameGeometry(
				// Ширина кадра
				width,
				// Высота кадра
				height,
				// Угол поворота
				rotation,
				// Фронтальная камера — картинку надо отражать по горизонтали
				SelectedCameraId == "front");

			// Собираем кадр для подписчиков
			var frame = new CameraFrame(
				// Пиксели
				data,
				// Ширина
				width,
				// Высота
				height,
				// Шаг строки: наш массив плотный, поэтому width * 4
				width * 4,
				// Формат: CameraX отдал RGBA
				CameraPixelFormat.Rgba,
				// Геометрия
				geometry,
				// Метка времени в мс с начала эпохи Unix — по ней можно считать реальный FPS
				DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

			// Рассылаем кадр подписчикам (ViewModel). Вызывается с фонового потока — это нормально,
			// ViewModel сам переключается на UI-поток через MainThread.BeginInvokeOnMainThread
			RaiseFrame(frame);
		}
		catch
		{
			// Обработчик кадров — «горячий» путь, вызывается 15–30 раз в секунду.
			// Исключение здесь сорвало бы весь поток анализа, поэтому молча пропускаем битый кадр
		}
	}

	// Адаптер между интерфейсом CameraX ImageAnalysis.IAnalyzer и нашим C#-методом.
	// Почему отдельный класс: CameraX — Java-библиотека, интерфейс помечен атрибутами Java,
	// поэтому делегат реализовать нельзя, нужен класс-наследник Java.Lang.Object.
	// Вложенный и sealed — ему не нужна видимость наружу; класс публичный, потому что
	// генератору Java-пиров (JCW) так надёжнее: для непубличных вложенных типов рантайм
	// иногда не находит peer и падает с «no Java peer type found»
	public sealed class CameraAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
	{
		// Ссылка на наш обработчик кадра (метод ProcessFrame внешнего класса)
		private readonly Action<IImageProxy> _onFrame;

		// Конструктор адаптера: принимает делегат обработки кадра
		public CameraAnalyzer(Action<IImageProxy> onFrame)
		{
			// Сохраняем ссылку на обработчик
			_onFrame = onFrame;
			System.Diagnostics.Debug.WriteLine("[AndroidCameraService] Конструктор CameraAnalyzer выполнен (Java peer найден)");
		}

		// Метод, который CameraX вызывает для каждого кадра (на потоке из SetAnalyzer)
		public void Analyze(IImageProxy? image)
		{
			// Защита от null: CameraX в теории может передать пустой вызов
			if (image is null)
			{
				return;
			}

			try
			{
				// Обрабатываем кадр: копируем пиксели и рассылаем подписчикам
				_onFrame(image);
			}
			catch
			{
				// Исключение не должно пробрасываться в CameraX — иначе он может остановить поток анализа
			}
			finally
			{
				// ОБЯЗАТЕЛЬНО освобождаем кадр. Если этого не сделать, через несколько кадров
				// закончится пул буферов и камера перестанет отдавать изображение.
				// Именно поэтому тело выше обёрнуто в try/finally, а не в простой try
				image.Close();
			}
		}
	}
}
