// Подключаем наши модели данных (CameraOption, CameraFrame, CameraFrameGeometry)
using MauiBluethooth.Models.Camera;
// Подключаем базовый камерный сервис и его интерфейс
using MauiBluethooth.Services;
// Подключаем WinUI-контрол MediaPlayerElement — нативный элемент предпросмотра в Windows
using Microsoft.UI.Xaml.Controls;
// Подключаем перечисление режима масштабирования видео (Stretch.UniformToFill)
using Microsoft.UI.Xaml.Media;
// Подключаем перечисление Panel — позволяет определить, фронтальная камера или задняя
using Windows.Devices.Enumeration;
// Подключаем BitmapPixelFormat для проверки формата пикселей полученного кадра
using Windows.Graphics.Imaging;
// Подключаем основной API камеры Windows: MediaCapture, MediaCaptureInitializationSettings
using Windows.Media.Capture;
// Подключаем API покадрового доступа к камере: MediaFrameSourceGroup, MediaFrameReader, событие FrameArrived
using Windows.Media.Capture.Frames;
// Подключаем MediaSource — мост между MediaFrameSource и MediaPlayer для показа видео
using Windows.Media.Core;
// Подключаем MediaEncodingSubtypes (формат кадра BGRA8) и VideoRotation
using Windows.Media.MediaProperties;
// Подключаем MediaPlayer — воспроизводит поток кадров в окне
using Windows.Media.Playback;
// Подключаем Buffer и DataReader для копирования пикселей из SoftwareBitmap в byte[]
using Windows.Storage.Streams;

// Объявляем пространство имён Windows-реализаций сервисов
namespace MauiBluethooth.Platforms.Windows.Services;

// Реализация камерного сервиса на Windows поверх WinRT-камеры: MediaCapture + MediaFrameReader.
// В Windows используются ДВА независимых механизма:
//   1) MediaFrameReader — выдаёт нам сырые кадры для обработки (оверлей, распознавание);
//   2) MediaPlayer + MediaSource — показывает живое видео пользователю.
// Один и тот же MediaFrameSource feeding оба механизма — это стандартный приём в WinRT, он не «съедает» кадры дважды
public sealed class WindowsCameraService : CameraServiceBase
{
	// Кэш «идентификатор камеры → реальные объекты Windows».
	// Идентификатор камеры в нашей модели — строка, а MediaCapture нужен живой MediaFrameSourceGroup,
	// поэтому найденные при инициализации объекты сохраняем отдельно.
	// StringComparer.Ordinal важен: идентификаторы Windows чувствительны к регистру и содержат «:»
	private readonly Dictionary<string, CameraSelection> _selections = new(StringComparer.Ordinal);

	// Нативный элемент предпросмотра (показывает видео). Создаётся лениво, один раз на приложение
	private MediaPlayerElement? _previewElement;

	// Главный объект доступа к камере. Пока он не освобождён, камера занята нашим приложением
	private MediaCapture? _mediaCapture;

	// Источник кадров: один кадр одновременно идёт в MediaFrameReader и в MediaPlayer
	private MediaFrameSource? _frameSource;

	// Читатель кадров: по нему приходят события FrameArrived с сырыми пикселями
	private MediaFrameReader? _frameReader;

	// Источник для MediaPlayer, созданный из MediaFrameSource
	private MediaSource? _mediaSource;

	// Плеер, воспроизводящий живой поток в MediaPlayerElement
	private MediaPlayer? _mediaPlayer;

	// На сколько градусов нужно повернуть кадр для правильной ориентации (определяется при старте)
	private int _rotationDegrees;

	// Нужно ли отражать кадр по горизонтали (фронтальная камера, либо камера сама требует зеркала)
	private bool _isMirrored;

	// Флаг: читатель кадров реально запущен. Нужен, чтобы при остановке НЕ вызывать StopAsync
	// у незапущенного ридера — Windows на это бросает исключение
	private bool _frameReaderStarted;

	// Создаёт нативный элемент предпросмотра
	protected override object CreatePlatformPreviewCore()
	{
		// Создаём MediaPlayerElement только один раз: он привязан к нашему MediaPlayer при старте,
		// и пересоздание элемента на каждой навигации привело бы к чёрному экрану
		_previewElement ??= new MediaPlayerElement
		{
			// Автовоспроизведение: MediaPlayer сам начнёт показывать поток (важно для live-видео)
			AutoPlay = true,

			// Прячем стандартные кнопки play/pause — пользователю не нужно управлять камерой вручную
			AreTransportControlsEnabled = false,

			// UniformToFill: видео занимает всё пространство, обрезая лишнее по центру.
			// Именно этот режим соответствует логике наложения оверлея (см. CameraOverlayDrawable)
			Stretch = Stretch.UniformToFill
		};

		// Возвращаем нативный элемент как object — тип платформенный
		return _previewElement;
	}

	// Одноразовая инициализация: находим все камеры устройства и запоминаем их источники
	protected override async Task InitializePlatformAsync()
	{
		// Очищаем кэш предыдущей инициализации (повторный запуск после обновления списка устройств)
		_selections.Clear();

		// Собираем список камер, доступных приложению (учитывая разрешения из манифеста)
		var options = new List<CameraOption>();

		// Запрашиваем у системы группы источников кадров. Асинхронно — обращается к драйверу камеры
		var groups = await MediaFrameSourceGroup.FindAllAsync();

		// Каждая группа — это физическое устройство (камера), у которого может быть несколько
		// источников (цветной сенсор, сенсор глубины, ИК-сенсор). Нам нужен один цветной
		foreach (var group in groups)
		{
			// Сначала ищем идеальный источник: цветной И с типом VideoPreview (поток предпросмотра)
			var sourceInfo = group.SourceInfos.FirstOrDefault(IsVideoPreviewSource)

				// Если VideoPreview нет (редко, но бывает у внешних веб-камер), берём любой цветной:
				// VideoPreview или VideoRecord
				?? group.SourceInfos.FirstOrDefault(IsSupportedVideoSource);

			// Устройство без подходящего цветного источника (например, только ИК-сенсор) пропускаем
			if (sourceInfo is null)
			{
				continue;
			}

			// Формируем наш идентификатор камеры. Составной (группа:источник), потому что одна
			// физическая камера может давать несколько разных потоков
			var id = $"{group.Id}:{sourceInfo.Id}";

			// Пытаемся узнать человекочитаемое имя устройства
			var name = sourceInfo.DeviceInformation?.Name;

			// Имя устройства может отсутствовать (null) — тогда пробуем имя группы
			if (string.IsNullOrWhiteSpace(name))
			{
				name = group.DisplayName;
			}

			// Если и у группы имя пустое, показываем нейтральное «Камера N» по порядковому номеру
			if (string.IsNullOrWhiteSpace(name))
			{
				name = $"Камера {options.Count + 1}";
			}

			// Определяем, фронтальная камера или задняя (влияет на выбор по умолчанию и на зеркалирование)
			var lensFacing = GetLensFacing(sourceInfo, name);

			// Сохраняем «сырые» объекты Windows в кэш по нашему идентификатору
			_selections[id] = new CameraSelection(group, sourceInfo, lensFacing);

			// И добавляем камеру в список для UI
			options.Add(new CameraOption(id, name, lensFacing));
		}

		// Передаём список в базовый класс: он выберет камеру по умолчанию (заднюю, если есть)
		SetCameraOptions(options);
	}

	// Запуск камеры: инициализируем MediaCapture, создаём ридер кадров и включаем показ видео
	protected override async Task StartPlatformAsync()
	{
		// Без элемента предпросмотра показывать кадры некуда
		if (_previewElement is null)
		{
			throw new InvalidOperationException("Windows camera preview is not initialized");
		}

		// По выбранному идентификатору достаём сохранённые объекты Windows.
		// Если камеру вытащили из устройства после инициализации, поиск провалится — это ожидаемая ошибка
		if (SelectedCameraId is null || !_selections.TryGetValue(SelectedCameraId, out var selection))
		{
			throw new InvalidOperationException("Selected Windows camera is unavailable");
		}

		try
		{
			// Создаём НОВЫЙ MediaCapture на каждый запуск.
			// Почему не переиспользуем: после освобождения MediaCapture его нельзя «оживить»,
			// а у Windows объект камеры одноразовый — так устроена модель владения устройством
			var mediaCapture = new MediaCapture();

			// Готовим настройки инициализации
			var settings = new MediaCaptureInitializationSettings
			{
				// Указываем конкретную камеру (а не любую доступную) — так мы уважаем выбор пользователя
				SourceGroup = selection.Group,

				// Cpu — кадры лежат в обычной памяти, и мы можем их читать.
				// (Direct3D-режим быстрее, но требует работы с GPU-поверхностями — здесь не нужен)
				MemoryPreference = MediaCaptureMemoryPreference.Cpu,

				// Видеорежим: без этого MediaCapture откроет камеру в фоторежиме
				StreamingCaptureMode = StreamingCaptureMode.Video
			};

			// Открываем камеру с этими настройками. Асинхронно: драйверу нужно время на инициализацию
			await mediaCapture.InitializeAsync(settings);

			// Достаём источник цветных кадров открытой камеры
			var frameSource = mediaCapture.FrameSources[selection.SourceInfo.Id];

			// Создаём читатель кадров в формате BGRA8.
			// BGRA8 выбран потому что его SoftwareBitmap напрямую копируется в byte[] без конвертации
			var frameReader = await mediaCapture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8);

			// Сохраняем объекты в поля ДО подписки на событие: так обработчик кадров увидит актуальные поля
			_mediaCapture = mediaCapture;
			_frameSource = frameSource;
			_frameReader = frameReader;

			// Определяем угол поворота кадра. GetPreviewRotation возвращает enum VideoRotation,
			// приводим его к градусам один раз здесь, а не в обработчике каждого кадра
			_rotationDegrees = ToRotationDegrees(mediaCapture.GetPreviewRotation());

			// Определяем зеркальность: фронтальная камера всегда зеркальная, плюс сама камера
			// может сообщить о необходимости зеркала (GetPreviewMirroring)
			_isMirrored = selection.LensFacing == CameraLensFacing.Front || mediaCapture.GetPreviewMirroring();

			// Подписываемся на кадры. Событие вызывается системой на отдельном потоке, 15–30 раз в секунду
			frameReader.FrameArrived += OnFrameArrived;

			// Создаём MediaSource из источника кадров — это «мостик» от камеры к видеоплееру.
			// Благодаря ему MediaPlayer показывает кадры, которые отдаёт та же камера
			var mediaSource = MediaSource.CreateFromMediaFrameSource(frameSource);

			// Создаём плеер с настройками для трансляции в реальном времени
			var mediaPlayer = new MediaPlayer
			{
				// Автовоспроизведение
				AutoPlay = true,

				// КРИТИЧНО для камеры: отключает буферизацию и «догоняющие» покадровое воспроизведение.
				// Без этого MediaPlayer копит кадры в буфере и картинка отстаёт от реальности на секунды
				RealTimePlayback = true
			};

			// Привязываем плеер к источнику
			mediaPlayer.Source = mediaSource;

			// Сохраняем объекты в поля для последующей остановки
			_mediaSource = mediaSource;
			_mediaPlayer = mediaPlayer;

			// Отдаём плеер элементу предпросмотра: теперь в UI видно живое видео
			_previewElement.SetMediaPlayer(mediaPlayer);

			// Запускаем читатель кадров. С этого момента приходят события FrameArrived
			var startStatus = await frameReader.StartAsync();

			// Windows может не запустить ридер (камера занята другим приложением, нет ресурсов).
			// Проверяем статус и падаем с понятной ошибкой
			if (startStatus != MediaFrameReaderStartStatus.Success)
			{
				throw new InvalidOperationException($"Windows frame reader failed with status {startStatus}");
			}

			// Ридер действительно запущен — запоминаем, чтобы при остановке вызвать StopAsync ровно один раз
			_frameReaderStarted = true;

			// Явно стартуем воспроизведение (на случай если AutoPlay не сработал из-за порядка вызовов)
			mediaPlayer.Play();
		}
		catch
		{
			// Запуск упал на промежуточном шаге (например, InitializeAsync не прошёл) —
			// могли остаться «полуоткрытые» объекты. Немедленно освобождаем их
			try
			{
				await StopPlatformAsync();
			}
			catch
			{
				// Ошибка при уборке не должна замаскировать исходную ошибку запуска
			}

			// Пробрасываем исходное исключение наверх: базовый класс превратит его в CameraStartResult
			// с текстом, который увидит пользователь
			throw;
		}
	}

	// Остановка камеры и освобождение всех нативных ресурсов.
	// Порядок освобождения важен: сначала источник кадров, потом видео, и только потом сама камера.
	// Иначе Windows сочтёт, что камера всё ещё используется, и следующий запуск провалится
	protected override async Task StopPlatformAsync()
	{
		// ШАГ 1. Забираем читатель кадров в локальную переменную и СРАЗУ обнуляем поле.
		// Так повторный вызов Stop (например, из обработчика DisconnectHandler) увидит null
		// и не попытается остановить уже освобождённый объект
		var frameReader = _frameReader;
		_frameReader = null;

		// Запоминаем, был ли ридер запущен, и сбрасываем флаг
		var frameReaderStarted = _frameReaderStarted;
		_frameReaderStarted = false;

		// Если ридер вообще был
		if (frameReader is not null)
		{
			// Сначала отписываемся от события кадров.
			// Это обязательно ДО остановки: иначе в очереди останутся события,
			// а обработчик попытается читать уже освобождённый кадр
			frameReader.FrameArrived -= OnFrameArrived;

			// Останавливаем поток кадров, но только если он был запущен:
			// StopAsync на незапущенном MediaFrameReader бросает исключение
			try
			{
				if (frameReaderStarted)
				{
					await frameReader.StopAsync();
				}
			}
			catch
			{
				// Исключение при остановке игнорируем: продолжаем освобождать остальные ресурсы
			}

			// Освобождаем ридер
			try
			{
				frameReader.Dispose();
			}
			catch
			{
				// Ошибка Dispose тоже не критична — объект станет мусором и будет собран GC
			}
		}

		// ШАГ 2. Забираем плеер и обнуляем поле (защита от повторного вызова)
		var mediaPlayer = _mediaPlayer;
		_mediaPlayer = null;

		// Если плеер был
		if (mediaPlayer is not null)
		{
			// Останавливаем воспроизведение и отвязываем источник.
			// Именно в этом порядке: сначала пауза, потом сброс Source
			try
			{
				mediaPlayer.Pause();

				// Source = null разрывает связь с MediaSource, иначе источник не освободится
				mediaPlayer.Source = null;
			}
			catch
			{
				// Плеер мог уже быть в состоянии сбоя — освобождаем дальше
			}
		}

		// Отвязываем плеер от элемента предпросмотра.
		// SetMediaPlayer(null) убирает картинку с экрана, иначе UI покажет последний замерший кадр
		if (_previewElement is not null && mediaPlayer is not null)
		{
			try
			{
				_previewElement.SetMediaPlayer(null);
			}
			catch
			{
				// Элемент мог быть уже уничтожен навигацией — не критично
			}
		}

		// ШАГ 3. Забираем MediaSource и обнуляем поле
		var mediaSource = _mediaSource;
		_mediaSource = null;

		// Если источник был — освобождаем его
		if (mediaSource is not null)
		{
			try
			{
				mediaSource.Dispose();
			}
			catch
			{
				// Игнорируем: главное — не оставить удержание камеры
			}
		}

		// Теперь, когда источник отпущен, освобождаем сам плеер
		if (mediaPlayer is not null)
		{
			try
			{
				mediaPlayer.Dispose();
			}
			catch
			{
				// Игнорируем
			}
		}

		// ШАГ 4. Забираем MediaCapture и обнуляем поле — освобождение самой камеры
		var mediaCapture = _mediaCapture;
		_mediaCapture = null;

		// Источник кадров принадлежит MediaCapture, поэтому обнуляем и его
		_frameSource = null;

		// Сбрасываем кэшированную геометрию: она относилась к конкретному запуску и больше недействительна
		_rotationDegrees = 0;
		_isMirrored = false;

		// Освобождаем MediaCapture — только теперь камера реально доступна другим приложениям
		if (mediaCapture is not null)
		{
			try
			{
				mediaCapture.Dispose();
			}
			catch
			{
				// Игнорируем: скорее всего, камера уже была освобождена системой
			}
		}
	}

	// Обработчик события «пришёл кадр». Вызывается системой на отдельном потоке, 15–30 раз в секунду
	private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
	{
		try
		{
			// Берём ПОСЛЕДНИЙ доступный кадр и сразу освобождаем его (using).
			// TryAcquireLatestFrame может вернуть null, если кадры сейчас недоступны — это штатная ситуация
			using var frame = sender.TryAcquireLatestFrame();

			// Достаём видеокадр из конверта
			var videoFrame = frame?.VideoMediaFrame;

			// Достаём CPU-растровое изображение и сразу освобождаем его (using):
			// без этого через несколько кадров закончится пул буферов и поток встанет
			using var bitmap = videoFrame?.SoftwareBitmap;

			// Проверяем, что кадр есть и что он в ожидаемом формате.
			// Если формат отличается от запрошенного BGRA8, конвертировать будем в будущей версии —
			// пока просто пропускаем такой кадр
			if (bitmap is null || bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
			{
				return;
			}

			// Ширина кадра в пикселях
			var width = bitmap.PixelWidth;

			// Высота кадра в пикселях
			var height = bitmap.PixelHeight;

			// Шаг строки: у BGRA8 всегда ровно 4 байта на пиксель.
			// checked — при переполнении int будет выброшено исключение, а не создаст неверный массив
			var stride = checked(width * 4);

			// Готовим массив под кадр. checked на размере — защита от выделения гигабайтов при кривых метаданных
			var data = new byte[checked(stride * height)];

			// Создаём буфер нужного размера для копирования из SoftwareBitmap.
			// Конструктор ожидает uint, поэтому приводим явно
			var buffer = new global::Windows.Storage.Streams.Buffer((uint)data.Length);

			// Копируем пиксели из растрового изображения в буфер (это быстрый memcpy нативного кода)
			bitmap.CopyToBuffer(buffer);

			// Оборачиваем буфер в DataReader для чтения в managed-массив.
			// DataReader ссылается на буфер, поэтому его нужно освободить (using)
			using var dataReader = DataReader.FromBuffer(buffer);

			// Копируем байты буфера в наш массив
			dataReader.ReadBytes(data);

			// Собираем геометрию кадра из значений, вычисленных при старте камеры
			var geometry = new CameraFrameGeometry(width, height, _rotationDegrees, _isMirrored);

			// Собираем кадр для подписчиков
			var frameData = new CameraFrame(
				// Пиксели
				data,
				// Ширина
				width,
				// Высота
				height,
				// Шаг строки
				stride,
				// Формат: Windows отдаёт BGRA — порядок каналов обратный по сравнению с Android
				CameraPixelFormat.Bgra,
				// Геометрия
				geometry,
				// Метка времени в мс с начала эпохи Unix — по ней можно считать реальный FPS
				DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

			// Рассылаем кадр подписчикам (ViewModel). Вызывается с фонового потока Windows
			RaiseFrame(frameData);
		}
		catch
		{
			// Горячий путь 15–30 раз в секунду: исключение здесь остановило бы весь поток кадров.
			// Молча пропускаем проблемный кадр и ждём следующий
		}
	}

	// Проверяет, что источник — идеальный для предпросмотра: цветной И типа VideoPreview.
	// Используется как предикат для FirstOrDefault, поэтому метод статический и возвращает bool
	private static bool IsVideoPreviewSource(MediaFrameSourceInfo sourceInfo) =>

		// SourceKind == Color отсекает сенсоры глубины и инфракрасные
		sourceInfo.SourceKind == MediaFrameSourceKind.Color

		// MediaStreamType == VideoPreview отсекает потоки записи (они часто другого разрешения)
		&& sourceInfo.MediaStreamType == MediaStreamType.VideoPreview;

	// Запасной вариант: любой цветной источник, который умеет отдавать видео
	private static bool IsSupportedVideoSource(MediaFrameSourceInfo sourceInfo) =>

		// Только цветные источники
		sourceInfo.SourceKind == MediaFrameSourceKind.Color

		// Либо предпросмотр, либо запись видео
		&& sourceInfo.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;

	// Определяет сторону объектива камеры: фронтальная или задняя.
	// Нужна для выбора камеры по умолчанию и для решения о зеркалировании картинки
	private static CameraLensFacing GetLensFacing(MediaFrameSourceInfo sourceInfo, string name)
	{
		// Спрашиваем у устройства, где оно физически расположено.
		// Для встроенных камер ноутбука Windows знает: Front (над экраном) или Back (над клавиатурой)
		var panel = sourceInfo.DeviceInformation?.EnclosureLocation?.Panel;

		// Камера над экраном → фронтальная
		if (panel == global::Windows.Devices.Enumeration.Panel.Front)
		{
			return CameraLensFacing.Front;
		}

		// Камера напротив пользователя → задняя
		if (panel == global::Windows.Devices.Enumeration.Panel.Back)
		{
			return CameraLensFacing.Rear;
		}

		// Для внешних веб-камер Panel == Unknown. Тогда определяем по имени устройства.
		// ToLowerInvariant — чтобы сравнение работало независимо от регистра в названии драйвера
		var normalizedName = name.ToLowerInvariant();

		// Ищем в имени признаки фронтальной камеры:
		// «front» — типичное имя встроенной камеры;
		// «user» — как в Intel User Facing Camera. Слова в кавычках: это часть имени, а не «любой символ»
		return normalizedName.Contains("front", StringComparison.Ordinal)
			|| normalizedName.Contains("user", StringComparison.Ordinal)

			// Найдено → фронтальная
			? CameraLensFacing.Front

			// Иначе считаем задней: для веб-камер это разумное умолчание
			: CameraLensFacing.Rear;
	}

	// Приводит enum VideoRotation из Windows к градусам.
	// Сделано один раз при старте, чтобы в обработчике каждого кадра не делать проверку enum
	private static int ToRotationDegrees(VideoRotation rotation) => rotation switch
	{
		// Поворот на 90° по часовой стрелке
		VideoRotation.Clockwise90Degrees => 90,

		// Поворот на 180°
		VideoRotation.Clockwise180Degrees => 180,

		// Поворот на 270° (= 90° против часовой стрелки)
		VideoRotation.Clockwise270Degrees => 270,

		// Нет поворота (или неожиданное значение) — 0°. «_» — это default в switch-выражении
		_ => 0
	};

	// Внутренняя запись: «сырые» объекты Windows для одной камеры.
	// Нужна, потому что наш публичный CameraOption содержит только строки, а MediaCapture
	// требует настоящие MediaFrameSourceGroup и MediaFrameSourceInfo.
	// record — потому что это просто неизменяемая связка данных, её удобно сравнивать и передавать целиком
	private sealed record CameraSelection(
		// Группа источников (физическое устройство) — передаётся в MediaCaptureInitializationSettings
		MediaFrameSourceGroup Group,

		// Конкретный источник внутри группы — по его Id мы достаём MediaFrameSource у открытой камеры
		MediaFrameSourceInfo SourceInfo,

		// Вычисленная сторона объектива: фронтальная или задняя
		CameraLensFacing LensFacing);
}
