// Подключаем пространство имён с коллекцией ObservableCollection (автообновляемая коллекция для привязок)
using System.Collections.ObjectModel;
// Подключаем базовые классы MVVM (ObservableObject, атрибут ObservableProperty)
using CommunityToolkit.Mvvm.ComponentModel;
// Подключаем компоненты для RelayCommand (команды, вызываемые из XAML)
using CommunityToolkit.Mvvm.Input;
// Подключаем наши модели данных камеры (CameraOption, CameraFrame, CameraFrameGeometry)
using MauiBluethooth.Models.Camera;
// Подключаем наши модели данных распознавания (DetectedObject, ModelDescriptor, DetectionDiagnostics)
using MauiBluethooth.Models.Detection;
// Подключаем модель данных Bluetooth-устройства (нужна для обработчика DeviceDiscovered)
using MauiBluethooth.Models;
// Подключаем интерфейс камерного сервиса
using MauiBluethooth.Services;
// Подключаем детектор: он превращает кадры камеры в рамки
using MauiBluethooth.Services.Detection;
// Подключаем передатчик распознанных объектов по Bluetooth
using MauiBluethooth.Services.Detection.Transmission;
// Подключаем модель представления устройства из раздела «Устройства»:
// она уже умеет показывать имя и адрес и используется в общем списке устройств
using MauiBluethooth.Presentation.Devices.ViewModels;
// Подключаем MainThread — он нужен, чтобы менять свойства привязки только в UI-потоке
using Microsoft.Maui.ApplicationModel;

// Объявляем пространство имён для моделей представления раздела «Камера»
namespace MauiBluethooth.Presentation.Camera.ViewModels;

// Модель представления экрана камеры.
// partial — потому что атрибуты [ObservableProperty] и [RelayCommand] из CommunityToolkit.Mvvm
// кодогенерируют дополнительную часть класса. IDisposable нужен, чтобы отписаться от события кадров
public partial class CameraViewModel : ObservableObject, IDisposable
{
	// Ссылка на камерный сервис (инжектируется через конструктор из DI)
	private readonly ICameraService _cameraService;

	// Ссылка на Bluetooth-сервис: поиск, подключение и отправка.
	// Нужен здесь для выбора устройства, к которому будут уходить рамки
	private readonly IBluetoothService _bluetoothService;

	// Ссылка на передатчик распознанных объектов.
	// ViewModel лишь пробрасывает ему флаг IsTransmissionEnabled,
	// а сама вся логика отправки живёт в передатчике
	private readonly DetectionTransmitter _bluetoothTransmitter;

	// Ссылка на детектор. Он подписан на кадры камеры и считает по ним рамки.
	// Детектор — singleton, а ViewModel тоже singleton, так что подписка ровно одна
	private readonly ObjectDetector _detector;

	// Флаг: мы САМИ выставляем SelectedCamera при обновлении списка.
	// Нужен, чтобы не запускать переключение камеры в ответ на собственное же присваивание —
	// иначе после каждого запуска камера «переключалась бы сама на себя»
	private bool _suppressCameraSelection;

	// Момент последнего обновления FrameGeometry в миллисекундах (для троттлинга обновлений)
	private long _lastGeometryUpdate;

	// Момент последнего пришедшего снимка распознавания.
	// По нему считается FPS распознавания
	private long _lastDetectionTimestamp;

	// Количество кадров, учтённых в текущей секунде. Обнуляется раз в секунду
	private int _framesInCurrentSecond;

	// Смещение, с которого начался текущий отсчёт секунды
	private long _secondWindowStart;

	// Конструктор: DI внедряет камерный сервис, детектор, Bluetooth-сервис и передатчик
	public CameraViewModel(
		ICameraService cameraService,
		ObjectDetector detector,
		IBluetoothService bluetoothService,
		DetectionTransmitter bluetoothTransmitter)
	{
		// Сохраняем сервис в приватное поле
		_cameraService = cameraService;

		// Сохраняем детектор
		_detector = detector;

		// Сохраняем Bluetooth-сервис
		_bluetoothService = bluetoothService;

		// Сохраняем передатчик
		_bluetoothTransmitter = bluetoothTransmitter;

		// Подписываемся на события Bluetooth-сервиса.
		// Они приходят из фонового потока, поэтому каждый обработчик
		// обязан переносить изменения свойств в UI-поток
		_bluetoothService.DeviceDiscovered += OnBtDeviceDiscovered;
		_bluetoothService.DiscoveryFinished += OnBtDiscoveryFinished;
		_bluetoothService.ConnectionChanged += OnBtConnectionChanged;

		// Подписываемся на кадры. Событие приходит с фонового потока платформы, 15–30 раз в секунду.
		// Обработчик (OnFrameReceived) специально ничего тяжёлого не делает — только троттлит геометрию
		_cameraService.FrameReceived += OnFrameReceived;

		// Подписываем детектор на кадры.
		// Порядок важен: наш обработчик добавлен первым и отработает раньше детектора.
		// Детектор считает медленно, а троттлинг геометрии откладывает работу в UI-поток
		// и на поток камеры почти не влияет
		_detector.AttachToCamera(_cameraService);

		// Подписываемся на результаты распознавания.
		// Эти события приходят с потока камеры, поэтому каждый обработчик
		// обязан переносить изменения свойств в UI-поток
		_detector.SnapshotReady += OnDetectionSnapshotReady;
		_detector.DiagnosticsReady += OnDetectionDiagnosticsReady;
		_detector.ModelFailed += OnDetectionModelFailed;

		// Наполняем список моделей для выпадающего списка и выбираем модель по умолчанию
		foreach (var model in _detector.AvailableModels)
		{
			// ObservableCollection, а не просто List: список должен быть виден интерфейсу сразу
			DetectionModels.Add(model);
		}

		// Выставляем модель по умолчанию и сразу синхронизируем с детектором:
		// иначе в списке было бы выбрано одно, а считала бы другое
		SelectedDetectionModel = _detector.SelectedModel;
	}

	// [ObservableProperty] генерирует публичное свойство Detections (текущий список рамок)
	[ObservableProperty]

	// Список найденных на последнем кадре объектов. Именно его рисует оверлей.
	// Свойство помечено атрибутом ради уведомления UI: оверлей перерисовывается
	// по событию PropertyChanged, ему не нужно знать о самом детекторе
	private IReadOnlyList<DetectedObject> _detections = [];

	// Список моделей распознавания для Picker
	public ObservableCollection<ModelDescriptor> DetectionModels { get; } = new();

	// Журнал зафиксированных результатов распознавания для страницы заданий.
	//
	// Зачем нужен и чем отличается от Detections: Detections — это состояние «прямо сейчас»,
	// оно заменяется каждый кадр, а журнал накапливает историю
	public ObservableCollection<DetectionTaskResult> DetectionHistory { get; } = new();

	// Максимальное число строк в журнале.
	//
	// Ограничение обязательно: история растёт вечно, а она живёт в памяти всю сессию.
	// При 15 кадрах в секунду без ограничения это десятки тысяч строк,
	// и приложение рано или поздно убьёт система из-за нехватки памяти.
	// 200 строк — это несколько минут работы и примерно полторы тысячи DetectedObject
	private const int MaxHistoryEntries = 200;

	// Подпись результата ПРЕДЫДУЩЕГО кадра.
	//
	// По ней отсекаются повторы: пока кадр показывает то же самое, новую строку
	// в журнал добавлять незачем. Сравнивается не сам список, а его строковое
	// представление — это заметно дешевле, чем сравнивать списки поэлементно
	private string _lastHistorySignature = "";

	// Резервное поле выбранной модели.
	// Как и у камеры, сеттер написан вручную, потому что смена модели
	// означает асинхронную и долгую перечитывать сеть — а сеттер свойства не может ждать
	private ModelDescriptor? _selectedDetectionModel;

	// Выбранная модель распознавания
	public ModelDescriptor? SelectedDetectionModel
	{
		// Геттер отдаёт значение поля
		get => _selectedDetectionModel;

		// Сеттер вызывается из Picker
		set
		{
			// Если значение реально изменилось — начинаем переключение
			if (SetProperty(ref _selectedDetectionModel, value) && value is not null)
			{
				// Переключаем модель. Ждать здесь нельзя: загрузка сети занимает секунды,
				// а сеттер свойства синхронный. Поэтому запускаем задачу и не ждём её
				_ = SwitchDetectionModelAsync(value);
			}
		}
	}

	// [ObservableProperty] генерирует публичное свойство IsDetectionEnabled
	[ObservableProperty]

	// Флаг: включено ли распознавание. Выключается кнопкой, чтобы можно было
	// смотреть видео без вычислений и не тратить батарею
	private bool _isDetectionEnabled = true;

	// [ObservableProperty] генерирует публичное свойство ScoreThreshold
	[ObservableProperty]

	// Порог уверенности: рамки слабее этого порога не показываются.
	// По умолчанию 0,5 — типичное значение, при котором почти нет мусора
	private float _scoreThreshold = 0.5f;

	// [ObservableProperty] генерирует публичное свойство IsModelLoading
	[ObservableProperty]

	// Флаг: идёт загрузка модели. Пока он true, список моделей лучше заблокировать:
	// две одновременные загрузки одной сети это лишняя работа и лишняя память
	private bool _isModelLoading;

	// [ObservableProperty] генерирует публичное свойство IsModelPickerEnabled
	[ObservableProperty]

	// Флаг: доступен ли для выбора список моделей. Это НЕ простое «не IsModelLoading»:
	// отдельное свойство читается в XAML как готовое значение, и его не нужно
	// пересчитывать конвертером, которого в проекте нет
	private bool _isModelPickerEnabled = true;

	// [ObservableProperty] генерирует публичное свойство DetectionStatusText
	[ObservableProperty]

	// Текст о состоянии распознавания: «3 объекта», «распознавание выключено», «лицо не найдено»
	private string _detectionStatusText = "Распознавание не запущено";

	// [ObservableProperty] генерирует публичное свойство DetectionFps
	[ObservableProperty]

	// Сколько кадров в секунду успевает обработать детектор. Это главный показатель
	// «тяжести» выбранной модели: на телефоне fp32 может дать 3–5 fps, а int8 — 15–20
	private double _detectionFps;

	// [ObservableProperty] генерирует публичное свойство ModelErrorText
	[ObservableProperty]

	// Текст ошибки модели, если что-то пошло не так. Пустая строка означает «всё в порядке».
	// Сам текст нужен пользователю, а для видимости в XAML есть отдельный флаг HasModelError:
	// конвертера «строка не пустая → bool» в проекте нет, а заводить его ради одного поля
	// означало бы лишний класс и лишний файл
	private string _modelErrorText = "";

	// [ObservableProperty] генерирует публичное свойство HasModelError
	[ObservableProperty]

	// Показывать ли метку с ошибкой модели
	private bool _hasModelError;

	// Минимальный порог уверенности для ползунка.
	// Значение берётся у детектора, а не дублируется числом в XAML:
	// если однажды границы изменятся, ползунок разойдётся с тем, что реально проверяет детектор
	public float MinScoreThreshold => ObjectDetector.MinScoreThreshold;

	// Максимальный порог уверенности для ползунка
	public float MaxScoreThreshold => ObjectDetector.MaxScoreThreshold;

	// [ObservableProperty] генерирует публичное свойство IsTransmissionEnabled
	[ObservableProperty]

	// Флаг: включить ли передачу распознанных объектов в Bluetooth.
	// Переключатель меняет это свойство, а оно, в свою очередь, — флаг передатчика.
	// Дополнительной логики нет: «подача включена» — это ровно один флаг у передатчика
	private bool _isTransmissionEnabled;

	// Реакция на переключатель «Подача в Bluetooth». Вызывается фреймворком автоматически
	partial void OnIsTransmissionEnabledChanged(bool value)
	{
		// Пробрасываем значение в передатчик — таймер запустится или остановится сам
		_bluetoothTransmitter.IsTransmissionEnabled = value;
	}

	// Список найденных Bluetooth-устройств для Picker.
	// Переиспользуем DeviceItemViewModel из раздела «Устройства» — там уже собраны
	// имя, адрес и статус сопряжения, дублировать модель не нужно
	public ObservableCollection<DeviceItemViewModel> BluetoothDevices { get; } = [];

	// [ObservableProperty] генерирует публичное свойство IsBluetoothSearching
	[ObservableProperty]

	// [NotifyPropertyChangedFor] — при изменении флага обновить текст кнопки поиска
	[NotifyPropertyChangedFor(nameof(BluetoothSearchButtonText))]
	// Флаг: идёт ли поиск Bluetooth-устройств прямо сейчас
	private bool _isBluetoothSearching;

	// Текст кнопки поиска: «Стоп», если идёт поиск, иначе «Поиск».
	// Вычисляемое свойство — как на странице устройств
	public string BluetoothSearchButtonText => IsBluetoothSearching ? "Стоп" : "Поиск";

	// [ObservableProperty] генерирует публичное свойство IsBluetoothConnected
	[ObservableProperty]

	// Флаг: установлено ли Bluetooth-соединение. Управляет видимостью кнопки «Отключить»
	private bool _isBluetoothConnected;

	// [ObservableProperty] генерирует публичное свойство BluetoothStatusText
	[ObservableProperty]

	// Текст статуса Bluetooth: «не подключено», «идёт поиск», «подключено: HC-05» и т.д.
	private string _bluetoothStatusText = "Bluetooth: не подключено";

	// Резервное поле выбранного устройства. Сеттер написан вручную,
	// потому что выбор устройства должен запустить подключение
	private DeviceItemViewModel? _selectedBluetoothDevice;

	// Выбранное устройство в Picker. Установка значения запускает подключение
	public DeviceItemViewModel? SelectedBluetoothDevice
	{
		// Геттер отдаёт значение поля
		get => _selectedBluetoothDevice;

		// Сеттер вызывается из Picker, когда пользователь выбрал устройство
		set
		{
			// SetProperty сам заметит изменение. Реагируем только на реальную смену
			if (SetProperty(ref _selectedBluetoothDevice, value) && value is not null)
			{
				// Подключаемся к выбранному устройству.
				// Ждать в сеттере нельзя — запускаем задачу и не ждём её.
				// Метод сам обрабатывает ошибки и сам пишет статус
				_ = ConnectBluetoothAsync(value);
			}
		}
	}

	// [ObservableProperty] генерирует публичное свойство LastDiagnostics
	[ObservableProperty]

	// Последний снимок замеров. Нужен странице диагностики: она могла быть открыта
	// раньше, чем появился хоть один кадр
	private DetectionDiagnostics? _lastDiagnostics;

	// Отдаём сервис наружу: он нужен не только ViewModel, но и самому контролу CameraPreview
	// (XAML биндит {Binding CameraService}). Отдельное свойство, а не доступ к приватному полю
	public ICameraService CameraService => _cameraService;

	// Список камер для Picker. Именно ObservableCollection, а не List:
	// при его замене внутренних элементов интерфейс перерисовывается автоматически
	public ObservableCollection<CameraOption> CameraOptions { get; } = new();

	// Резервное поле выбранной камеры.
	// Свойство SelectedCamera не помечено [ObservableProperty], потому что у него есть
	// дополнительная логика в сеттере — реакция на смену камеры
	private CameraOption? _selectedCamera;

	// Выбранная камера. Сеттер написан вручную, потому что помимо уведомления UI
	// нужно ещё и переключить камеру в сервисе
	public CameraOption? SelectedCamera
	{
		// Геттер просто отдаёт значение поля
		get => _selectedCamera;

		// Сеттер вызывается из Picker, когда пользователь выбрал другую камеру
		set
		{
			// SetProperty сам сравнит новое и старое значение, обновит поле
			// и вызовет PropertyChanged. Если значение не изменилось — возвращает false
			if (SetProperty(ref _selectedCamera, value))
			{
				// Значение реально изменилось — реагируем на это
				OnSelectedCameraChanged(value);
			}
		}
	}

	// [ObservableProperty] генерирует публичное свойство IsStarting (идёт ли запуск камеры)
	[ObservableProperty]

	// Флаг: прямо сейчас камера запускается. Пока он true, повторные нажатия игнорируются
	private bool _isStarting;

	// [ObservableProperty] генерирует публичное свойство IsRunning (работает ли камера)
	[ObservableProperty]

	// Флаг: камера запущена и отдаёт кадры. Управляет видимостью кнопки «Остановить»
	private bool _isRunning;

	// [ObservableProperty] генерирует публичное свойство IsCameraPickerVisible (показывать ли выбор камеры)
	[ObservableProperty]

	// Флаг: показывать ли Picker выбора камеры. Скрываем, если камера всего одна — выбор бессмыслен
	private bool _isCameraPickerVisible;

	// [ObservableProperty] генерирует публичное свойство ShowPermissionSettings (показать кнопку настроек)
	[ObservableProperty]

	// Флаг: пользователь отказал в доступе к камере.
	// Повторный запрос разрешения уже не поможет, поэтому показываем кнопку «Открыть настройки»
	private bool _showPermissionSettings;

	// [ObservableProperty] генерирует публичное свойство StatusText (текст статуса под видео)
	[ObservableProperty]

	// Текст статуса: «Ожидание», «Запуск камеры...», «Камера запущена», текст ошибки и т.д.
	private string _statusText = "Ожидание";

	// [ObservableProperty] генерирует публичное свойство FrameGeometry (геометрия текущего кадра)
	[ObservableProperty]

	// Геометрия последнего кадра. По ней оверлей (CameraOverlayDrawable) рисует рамку
	// в тех же координатах, что и видео. null, пока кадров не было
	private CameraFrameGeometry? _frameGeometry;

	// [ObservableProperty] генерирует публичное свойство IsCameraTabSelected
	[ObservableProperty]

	// [NotifyPropertyChangedFor] — при смене вкладки обновить парное свойство IsControlsTabSelected.
	// Без этого вторая кнопка таббара не узнала бы, что её состояние изменилось
	[NotifyPropertyChangedFor(nameof(IsControlsTabSelected))]

	// Флаг: выбрана ли вкладка «Камера». По умолчанию открыта именно она —
	// пользователь должен сразу видеть видео, а не панель управления
	private bool _isCameraTabSelected = true;

	// Парное свойство: выбрана ли вкладка «Управление».
	// Вычисляемое, а не отдельное поле: вкладок ровно две, поэтому одно
	// всегда является отрицанием другого, и синхронизировать два поля не нужно
	public bool IsControlsTabSelected => !IsCameraTabSelected;

	// [RelayCommand] генерирует команду ShowCameraTabCommand (кнопка «Камера»)
	[RelayCommand]

	// Показать вкладку камеры
	private void ShowCameraTab() => IsCameraTabSelected = true;

	// [RelayCommand] генерирует команду ShowControlsTabCommand (кнопка «Управление»)
	[RelayCommand]

	// Показать вкладку управления
	private void ShowControlsTab() => IsCameraTabSelected = false;

	// Вызывается страницей из OnAppearing: страница открылась
	public async Task OnAppearingAsync()
	{
		// Сообщаем сервису, что страница видима.
		// Это условие в HandleAppResumeAsync: возвращаться из фона с камерой имеет смысл только здесь
		_cameraService.SetPageActive(true);

		// Запускаем модель распознавания.
		//
		// ЗАЧЕМ ЖДЕМ ИНИЦИАЛИЗАЦИЮ ДЕТЕКТОРА, А НАЧИНАЕМ С КАМЕРЫ.
		// Видео пользователь должен увидеть быстро, а модель может грузиться
		// несколько секунд. Порядок «камера, потом модель» даёт картинку сразу,
		// а рамки появляются через пару секунд, когда сеть уже готова.
		// Сам детектор при этом устроен так, что кадры до загрузки модели
		// спокойно пропускает: он проверяет модель на null и выходит
		await StartCameraAsync();

		// Показываем, что модель готовится
		SetModelLoading(true);

		// Показываем честный статус, а не пустоту
		DetectionStatusText = "Загрузка модели...";

		try
		{
			// Готовим детектор к работе и загружаем модель.
			//
			// ВАЖНО: работа уходит в Task.Run намеренно. Чтение сети — это
			// синхронный вызов нативного кода OpenCV, который занимает секунды.
			// Если вызвать его прямо из UI-потока, приложение замрёт: перестанет
			// отрисовываться и даже не отзовётся на касания.
			// Task.Run переносит всю цепочку на поток из пула потоков
			await Task.Run(() => _detector.InitializeAsync());
		}
		finally
		{
			// Крутилка гасится в любом случае — и при успехе, и при ошибке,
			// иначе интерфейс остался бы с вечно вращающимся индикатором
			SetModelLoading(false);
		}
	}

	// Реакция на переключатель «распознавание включено».
	//
	// CommunityToolkit.Mvvm сам вызывает этот метод после изменения свойства.
	// Он объявлен partial не просто так: без этой сигнатуры генератор не свяжет
	// его с изменением IsDetectionEnabled, и метод молча не вызвался бы
	partial void OnIsDetectionEnabledChanged(bool value)
	{
		// Просто передаём флаг в детектор.
		// Никакой своей логики: включение и выключение распознавания —
		// это ровно одна переменная у детектора
		_detector.IsEnabled = value;

		// Показываем пользователю, что именно происходит.
		// При выключении статус меняется сразу, а не после следующего кадра:
		// иначе на экране осталось бы «5 объектов» при выключенном распознавании
		if (!value)
		{
			// Распознавание выключено — рамок не будет
			DetectionStatusText = "Распознавание выключено";
		}
	}

	// Реакция на изменение ползунка порога уверенности.
	// Здесь просто передаём значение в детектор: пересчитывать рамки заново
	// не нужно, они пересчитаются на следующем же кадре
	partial void OnScoreThresholdChanged(float value)
	{
		// Передаём порог детектору
		_detector.ScoreThreshold = value;
	}

	// Вызывается страницей из OnDisappearing: пользователь ушёл на другой экран
	public async Task OnDisappearingAsync()
	{
		// Сообщаем сервису, что страница скрыта — при возврате из фона камера не поднимется
		_cameraService.SetPageActive(false);

		// Останавливаем камеру и освобождаем её.
		// Критично: если этого не сделать, камера останется занятой и другое приложение её не откроет
		await _cameraService.StopAsync();

		// Синхронизируем UI с реальным состоянием: кнопка «Остановить» должна исчезнуть
		IsRunning = false;

		// Выключаем передачу распознавания: камеры больше нет, отправлять нечего.
		// Сам Bluetooth-канал НЕ рвём — соединение переживает уход со страницы
		// (иначе переход на «Диагностику» или «Задания» обрывал бы линию)
		IsTransmissionEnabled = false;

		// Если в этот момент шёл поиск устройств — останавливаем его.
		// Закрытая страница не должна занимать Bluetooth-адаптер поиском
		if (IsBluetoothSearching)
			await StopBluetoothSearchAsync();
	}

	// [RelayCommand] генерирует команду StartCameraCommand для вызова из XAML
	[RelayCommand]

	// Команда «Запустить камеру». Прямая обёртка над общим методом запуска
	private Task StartCamera() => StartCameraAsync();

	// [RelayCommand] генерирует команду StopCameraCommand (кнопка «Остановить»)
	[RelayCommand]

	// Команда «Остановить камеру»
	private async Task StopCamera()
	{
		// Просим сервис освободить камеру
		await _cameraService.StopAsync();

		// Обновляем UI: камера больше не работает
		IsRunning = false;

		// Пишем понятный статус вместо технического
		StatusText = "Остановлено";
	}

	// [RelayCommand] генерирует команду OpenSettingsCommand (кнопка «Открыть настройки»)
	[RelayCommand]

	// Команда открытия системных настроек приложения — единственный способ выдать разрешение,
	// если пользователь уже отказал. Метод статический: ему не нужен доступ к полям ViewModel
	private static void OpenSettings() => AppInfo.ShowSettingsUI();

	// [RelayCommand] генерирует команду ToggleBluetoothSearchCommand (кнопка «Поиск»/«Стоп»)
	[RelayCommand]

	// Команда переключения поиска Bluetooth-устройств: одна кнопка «Поиск» и «Стоп»
	private async Task ToggleBluetoothSearch()
	{
		// Если поиск уже идёт — пользователь нажал «Стоп»
		if (IsBluetoothSearching)
		{
			// Останавливаем поиск и выходим
			await StopBluetoothSearchAsync();
			return;
		}

		// Запрашиваем разрешения на Bluetooth перед поиском
		if (!await _bluetoothService.RequestPermissionsAsync())
		{
			// Без разрешений поиск не начнётся — сообщаем, что нужно их выдать
			BluetoothStatusText = "Bluetooth: нет доступа. Проверьте разрешения.";
			return;
		}

		// Очищаем прошлый список найденных устройств
		BluetoothDevices.Clear();
		// Сообщаем о начале поиска
		BluetoothStatusText = "Bluetooth: идёт поиск...";
		// Взводим флаг поиска (кнопка меняется на «Стоп»)
		IsBluetoothSearching = true;

		// Оборачиваем в try/catch: запуск поиска может упасть (выключен адаптер и т.п.)
		try
		{
			// Запускаем поиск через сервис
			var started = await _bluetoothService.StartDiscoveryAsync();

			// Если не удалось запустить
			if (!started)
			{
				// Снимаем флаг и сообщаем о проблеме
				IsBluetoothSearching = false;
				BluetoothStatusText = "Bluetooth: не удалось запустить поиск.";
			}
		}
		catch (Exception ex)
		{
			// Показываем текст ошибки
			IsBluetoothSearching = false;
			BluetoothStatusText = $"Bluetooth: ошибка поиска: {ex.Message}";
		}
	}

	// [RelayCommand] генерирует команду DisconnectBluetoothCommand (кнопка «Отключить»)
	[RelayCommand]

	// Команда явного отключения от Bluetooth-устройства
	private async Task DisconnectBluetooth()
	{
		// Обрабатываем ошибки отключения
		try
		{
			// Просим сервис закрыть соединение. Статус обновит обработчик ConnectionChanged
			await _bluetoothService.DisconnectAsync();
		}
		catch (Exception ex)
		{
			// Показываем текст ошибки
			BluetoothStatusText = $"Bluetooth: ошибка отключения: {ex.Message}";
		}
	}

	// Подключение к выбранному устройству. Запускается сеттером SelectedBluetoothDevice
	private async Task ConnectBluetoothAsync(DeviceItemViewModel item)
	{
		// Во время поиска соединиться нельзя — останавливаем его
		if (IsBluetoothSearching)
			await StopBluetoothSearchAsync();

		// Показываем, что идёт подключение
		BluetoothStatusText = "Bluetooth: подключение...";

		// Обрабатываем ошибки подключения
		try
		{
			// Просим сервис установить соединение с выбранным устройством
			var ok = await _bluetoothService.ConnectAsync(item.Device);

			// Если подключились — сервис уже вызвал ConnectionChanged(true),
			// но статус всё равно обновим: вдруг обработчик события не успел
			BluetoothStatusText = ok ? $"Bluetooth: {item.Name}" : "Bluetooth: не удалось подключиться";
		}
		catch (Exception ex)
		{
			// Показываем текст ошибки
			BluetoothStatusText = $"Bluetooth: ошибка подключения: {ex.Message}";
		}
	}

	// Внутренний метод остановки поиска Bluetooth-устройств
	private async Task StopBluetoothSearchAsync()
	{
		// Обрабатываем ошибки остановки
		try
		{
			// Останавливаем поиск через сервис
			await _bluetoothService.StopDiscoveryAsync();
		}
		catch (Exception ex)
		{
			// Ошибка не фатальна — просто пишем в лог
			System.Diagnostics.Debug.WriteLine($"[CameraViewModel] Ошибка остановки поиска: {ex.Message}");
		}

		// Снимаем флаг в любом случае: поиск завершён или остановлен принудительно
		IsBluetoothSearching = false;
	}

	// Обработчик события «найдено Bluetooth-устройство». Приходит с фонового потока
	private void OnBtDeviceDiscovered(object? sender, BluetoothDeviceInfo device)
	{
		// Переносим обновление списка в UI-поток
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Ищем уже существующую запись с таким же адресом
			var existing = BluetoothDevices.FirstOrDefault(d => d.Address == device.Address);

			// Если записи ещё нет — добавляем новую
			if (existing is null)
			{
				BluetoothDevices.Add(new DeviceItemViewModel(device));
			}
			else
			{
				// Устройство уже в списке — обновляем его данные
				existing.Update(device);
			}
		});
	}

	// Обработчик события «поиск Bluetooth завершён». Приходит с фонового потока
	private void OnBtDiscoveryFinished(object? sender, EventArgs e)
	{
		// Переносим обновление UI в главный поток
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Снимаем флаг поиска
			IsBluetoothSearching = false;

			// Показываем понятный статус вместо «идёт поиск...»
			if (BluetoothStatusText == "Bluetooth: идёт поиск...")
				BluetoothStatusText = "Bluetooth: поиск завершён. Выберите устройство.";
		});
	}

	// Обработчик события «изменилось состояние Bluetooth-соединения». С фонового потока
	private void OnBtConnectionChanged(object? sender, bool connected)
	{
		// Переносим обновление UI в главный поток
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Обновляем флаг — кнопка «Отключить» видна только при соединении
			IsBluetoothConnected = connected;

			// Если соединение установлено
			if (connected)
			{
				// Показываем имя подключённого устройства (если оно ещё выбрано в списке)
				BluetoothStatusText = SelectedBluetoothDevice is not null
					? $"Bluetooth: {SelectedBluetoothDevice.Name}"
					: "Bluetooth: подключено";
			}
			else
			{
				// Соединение потеряно или закрыто.
				// Сразу выключаем передачу: отправлять в никуда бессмысленно
				IsTransmissionEnabled = false;

				// Показываем нейтральный статус
				BluetoothStatusText = "Bluetooth: не подключено";

				// Снимаем выбор устройства, чтобы пользователь мог подключиться заново
				SelectedBluetoothDevice = null;
			}
		});
	}

	// Освобождение ресурсов: вызывается, когда DI уничтожает сервис
	public void Dispose()
	{
		// ОБЯЗАТЕЛЬНО отписываемся от события кадров.
		// Без этого сервис-синглтон удерживал бы ссылку на ViewModel в памяти
		_cameraService.FrameReceived -= OnFrameReceived;

		// Отписываем детектор от кадров.
		// Это важнее предыдущей строки: пока детектор подписан на кадры камеры,
		// он продолжает считать по ним, даже когда страница давно закрыта.
		// Это означало бы постоянный расход батареи в фоне
		_detector.DetachFromCamera(_cameraService);

		// Отписываемся от событий детектора, чтобы ViewModel не удерживался событием
		_detector.SnapshotReady -= OnDetectionSnapshotReady;
		_detector.DiagnosticsReady -= OnDetectionDiagnosticsReady;
		_detector.ModelFailed -= OnDetectionModelFailed;

		// Отписываемся от событий Bluetooth-сервиса — тот же принцип, что и выше:
		// синглтон-сервис не должен удерживать ViewModel в памяти
		_bluetoothService.DeviceDiscovered -= OnBtDeviceDiscovered;
		_bluetoothService.DiscoveryFinished -= OnBtDiscoveryFinished;
		_bluetoothService.ConnectionChanged -= OnBtConnectionChanged;
	}

	// [ObservableProperty] генерирует публичное свойство IsTasksRefreshing
	[ObservableProperty]

	// Флаг: страница заданий показывает индикатор обновления.
	// Нужен для RefreshView, который сам не знает, заканчивается ли обновление:
	// если ему не сообщить об этом, он будет крутить индикатор вечно
	private bool _isTasksRefreshing;

	// [RelayCommand] генерирует команду RefreshTasksCommand
	[RelayCommand]

	// Обновление журнала заданий по кнопке.
	//
	// ЗАЧЕМ ЭТО НУЖНО, ЕСЛИ ДАННЫЕ И ТАК ПРИХОДЯТ САМИ.
	// Журнал пополняется по мере обработки кадров, и специально ничего нажимать
	// не нужно. Но RefreshView требует команды, иначе на кнопку «обновить»
	// ничего не происходит. Пользователь нажимает её привычным жестом и ждёт
	// осмысленного действия, поэтому команда должна хоть что-то делать.
	//
	// Здесь она просто закрывает индикатор обновления: к этому моменту журнал
	// уже содержит все накопленные результаты
	private void RefreshTasks()
	{
		// Снимаем индикатор обновления.
		// Синхронно, без задержки: искусственное «обновление» на полсекунды
		// выглядело бы как подвисание, а данные и так уже на экране
		IsTasksRefreshing = false;
	}

	// [RelayCommand] генерирует команду GoToDiagnosticsCommand
	[RelayCommand]

	// Переход на страницу диагностики: замеры по этапам обработки кадра
	private static Task GoToDiagnostics() => Shell.Current.GoToAsync("diagnostics");

	// [RelayCommand] генерирует команду GoToTasksCommand
	[RelayCommand]

	// Переход на страницу заданий: что именно распознано и с каким результатом
	private static Task GoToTasks() => Shell.Current.GoToAsync("tasks");

	// Переключает модель распознавания.
	//
	// Метод запускается из сеттера SelectedDetectionModel и сам по себе ничего
	// не возвращает вызывающему: сеттер свойства не может ждать
	private async Task SwitchDetectionModelAsync(ModelDescriptor model)
	{
		// Пока идёт переключение, блокируем список моделей.
		// Без этого пользователь мог бы тыкать по Picker во время загрузки,
		// и в очередь встали бы несколько параллельных загрузок сетей
		SetModelLoading(true);

		// Снимаем прежнюю ошибку модели: она относилась к другой модели.
		// Если новую модель тоже не удастся загрузить, ошибка появится снова
		ModelErrorText = "";
		HasModelError = false;

		// Показываем, что происходит
		DetectionStatusText = $"Загрузка: {model.DisplayName}...";

		try
		{
			// Загрузка модели — на потоке пула, по той же причине,
			// что и при инициализации: чтение ONCV-файла занимает секунды
			await Task.Run(() => _detector.SwitchModelAsync(model));
		}
		finally
		{
			// Снова разрешаем выбирать модель
			SetModelLoading(false);
		}
	}

	// Ставит флаг загрузки модели и заодно включает или выключает список моделей.
	//
	// Зачем отдельный метод, а не просто присваивание IsModelLoading.
	// «Идёт загрузка» и «список недоступен» — это два разных вопроса, но связаны
	// напрямую: список блокируется ровно тогда, когда идёт загрузка.
	// Связь выражена в одном месте, и её нельзя случайно сломать,
	// изменив IsModelLoading в одной ветке кода и забыв про другую
	private void SetModelLoading(bool value)
	{
		// Показываем или прячем крутилку
		IsModelLoading = value;

		// Блокируем или разблокируем список моделей
		IsModelPickerEnabled = !value;
	}

	// Обработчик события «детектор посчитал очередной кадр».
	//
	// ВАЖНО: событие приходит с потока камеры, а не из UI-потока.
	// Присваивать свойства привязки здесь нельзя: MAUI делает это не потокобезопасно,
	// и результатом будут случайные падения вида «доступ из другого потока»
	private void OnDetectionSnapshotReady(object? sender, DetectionSnapshot snapshot)
	{
		// Переносим обновление интерфейса в UI-поток.
		// BeginInvokeOnMainThread, а не InvokeOnMainThread: первый не ждёт выполнения,
		// и поток камеры сразу возвращается к работе. Ждать тут нельзя —
		// пока кадр обрабатывается, камера не может прислать следующий
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Кладём новый список рамок в свойство. Оверлей прочитает его при перерисовке
			Detections = snapshot.Objects;

			// Обновляем FPS по метке времени кадра
			UpdateDetectionFps(snapshot.FrameTimestampMilliseconds);

			// Пересчитываем текст статуса под текущее число находок.
			// У распознавания лиц и у поиска фигур разный смысл,
			// поэтому подпись зависит от типа модели
			DetectionStatusText = BuildDetectionStatus(snapshot);

			// Заносим результат в журнал, если он отличается от предыдущего
			AddToHistory(snapshot);
		});
	}

	// Обработчик события «обновилась диагностика». Также приходит с потока камеры
	private void OnDetectionDiagnosticsReady(object? sender, DetectionDiagnostics diagnostics)
	{
		// Страница диагностики читает это свойство, когда открыта.
		// Кладём снимок в UI-потоке и там же
		MainThread.BeginInvokeOnMainThread(() => LastDiagnostics = diagnostics);
	}

	// Обработчик события «с моделью что-то не так». Также приходит с потока камеры
	private void OnDetectionModelFailed(object? sender, string message)
	{
		// Текст ошибки показываем пользователю, а не пишем в лог.
		// Для пользователя важнее «модель не загрузилась», чем стек вызовов
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Сохраняем сообщение. Подробности детектора уже формируют
			// понятный текст, дублировать его в лог незачем
			ModelErrorText = message;

			// Показываем метку с ошибкой
			HasModelError = true;

			// И сразу убираем обещание рамок, которых не будет
			DetectionStatusText = "Распознавание недоступно";
		});
	}

	// Считает, сколько кадров в секунду обработал детектор.
	//
	// Зачем это вообще: ощущение «распознавание тормозит» почти всегда вызвано
	// низким FPS, а не самим алгоритмом. Пользователь не может это увидеть иначе,
	// а разработчику без этого числа не сказать, какая модель быстрее.
	// Считаем по скользящему окну в одну секунду: так значение живое,
	// но не дрожит на каждом кадре
	private void UpdateDetectionFps(long frameTimestampMilliseconds)
	{
		// Запоминаем момент последнего кадра
		_lastDetectionTimestamp = frameTimestampMilliseconds;

		// Увеличиваем счётчик кадров в текущем окне
		_framesInCurrentSecond++;

		// Сколько прошло с начала окна
		var windowDuration = frameTimestampMilliseconds - _secondWindowStart;

		// Окно ещё не истекло — ждём
		if (windowDuration < 1000)
		{
			// Слишком рано обновлять
			return;
		}

		// Окно истекло: считаем результат и начинаем новое окно.
		// Делим на секунды, а не берём «просто количество кадров»:
		// если между кадрами была пауза, честное FPS будет ниже,
		// и именно это значение покажет реальную отзывчивость
		DetectionFps = _framesInCurrentSecond * 1000.0 / windowDuration;

		// Обнуляем счётчик и переносим начало окна на текущий момент
		_framesInCurrentSecond = 0;
		_secondWindowStart = frameTimestampMilliseconds;
	}

	// Добавляет результат в журнал, если он отличается от предыдущего кадра.
	//
	// ЗАЧЕМ ТАК СЛОЖНО ИМЕННО РАДИ ЖУРНАЛА.
	// Кадры идут десятками в секунду, и без этой проверки журнал превратился бы
	// в тысячи одинаковых строк: человек стоит перед камерой, модель каждое
	// сообщение подтверждает одно и то же, а история растёт и растёт.
	// Практически полезна обратная ситуация: в кадре что-то изменилось —
	// кто-то вошёл, кто-то вышел, появилась фигура
	private void AddToHistory(DetectionSnapshot snapshot)
	{
		// Собираем подпись текущего результата: список названий через запятую.
		// Этого достаточно для сравнения, потому что нас интересует СОСТАВ,
		// а не точные координаты рамок: они всё равно меняются от кадра к кадру
		var signature = BuildSignature(snapshot);

		// Если результат не изменился — в журнал ничего не пишем
		if (signature == _lastHistorySignature)
		{
			// Тот же самый набор объектов, что и на прошлом кадре
			return;
		}

		// Запоминаем новую подпись
		_lastHistorySignature = signature;

		// Добавляем строку в начало журнала.
		// Insert(0, ...), а не Add: свежие результаты нужны наверху, а внизу
		// просто копятся. Читать список с конца пришлось бы прокручивать вниз
		DetectionHistory.Insert(0, new DetectionTaskResult
		{
			// Отмечаем время кадра
			TimestampMilliseconds = snapshot.FrameTimestampMilliseconds,

			// Какая модель дала результат
			ModelId = snapshot.ModelId,

			// Её читаемое название для заголовка
			ModelDisplayName = SelectedDetectionModel?.DisplayName ?? snapshot.ModelId,

			// Список находок. Снимок неизменяемый, так что ссылка безопасна
			Objects = snapshot.Objects
		});

		// Обрезаем журнал, если он перерос.
		//
		// RemoveAt в конце списка — самая дешёвая операция: элемент физически последний,
		// и индекс у него заранее известный. RemoveAt(0) был бы дороже,
		// потому что пришлось бы сдвигать все оставшиеся элементы
		while (DetectionHistory.Count > MaxHistoryEntries)
		{
			// Удаляем самый старый результат
			DetectionHistory.RemoveAt(DetectionHistory.Count - 1);
		}
	}

	// Строит подпись состава результата для сравнения с предыдущим кадром
	private static string BuildSignature(DetectionSnapshot snapshot)
	{
		// Собираем список названий
		var labels = new List<string>(snapshot.Objects.Count);

		// Проходим по всем находкам
		foreach (var item in snapshot.Objects)
		{
			// Добавляем название в подпись
			labels.Add(item.Label);
		}

		// Возвращаем склеенную подпись.
		// Порядок не сортируем: сеть выдаёт объекты примерно по убыванию уверенности,
		// и из-за этого порядок между кадрами может «прыгать» при одном и том же наборе.
		// Поэтому сортируем — иначе один и тот же результат будет считаться новым
		// только из-за перестановки одинаковых по смыслу элементов
		labels.Sort(System.StringComparer.Ordinal);

		// Склеиваем
		return string.Join(",", labels);
	}

	// Формирует текст статуса по результату распознавания.
	//
	// Зачем разные формулировки для разных моделей. «Объектов не найдено» и
	// «лиц не найдено» — это разные ситуации с разными действиями пользователя:
	// в первом случае, возможно, в кадре просто пусто, во втором стоит
	// повернуть голову или подойти ближе. Одна общая надпись «ничего не найдено»
	// не подсказывает пользователю, что делать
	private static string BuildDetectionStatus(DetectionSnapshot snapshot)
	{
		// Число находок
		var count = snapshot.Objects.Count;

		// Для лиц важнее само число лиц, поэтому пишем его явно
		if (snapshot.Kind == DetectionModelKind.Face)
		{
			// Нашлось несколько — показываем во множественном числе,
			// иначе «лиц: 3» читается неграмотно
			return count == 0
				? "Лиц не найдено"
				: count == 1
					? "Найдено 1 лицо"
					: $"Найдено лиц: {count}";
		}

		// Для объектов и фигур формулировка проще
		return count == 0
			? "Объектов не найдено"
			: $"Найдено объектов: {count}";
	}

	// Реакция на смену выбранной камеры пользователем
	private void OnSelectedCameraChanged(CameraOption? value)
	{
		// Проверяем два условия:
		// _suppressCameraSelection — присваивание сделали мы сами (обновление списка), реагировать не нужно;
		// value is not null — пустым значением сеттер не вызывается
		if (!_suppressCameraSelection && value is not null)
		{
			// Запускаем переключение «в фоне» (fire-and-forget).
			// Сеттер свойства не может быть async, поэтому задачу не ждём и исключения не наблюдаем:
			// сам метод ChangeCameraAsync обрабатывает свои ошибки и сам пишет статус
			_ = ChangeCameraAsync(value);
		}
	}

	// Общий метод запуска камеры (используется и командой, и OnAppearingAsync)
	private async Task StartCameraAsync()
	{
		// Защита от повторного запуска: пока камера включается ИЛИ уже включена — ничего не делаем.
		// Без этой проверки двойной тап по кнопке создал бы две гонки за камеру
		if (IsStarting || IsRunning)
		{
			return;
		}

		// Показываем индикатор загрузки и блокируем повторные нажатия
		IsStarting = true;

		// Информируем пользователя о том, что происходит
		StatusText = "Запуск камеры...";

		// Просим сервис запустить камеру. Он сам запросит разрешение, найдёт камеры и вернёт результат
		var result = await _cameraService.StartAsync();

		// Обновляем список камер: сервис обнаруживает их только при первом запуске
		RefreshCameraOptions();

		// Гасим индикатор загрузки
		IsStarting = false;

		// Состояние работы камеры берём ИЗ РЕЗУЛЬТАТА, а не оптимистично: запуск мог провалиться
		IsRunning = result.Success;

		// Показываем кнопку «Открыть настройки» только если дело именно в запрете разрешения.
		// При других ошибках (камера занята другим приложением) настройки не помогут
		ShowPermissionSettings = !result.Success && _cameraService.PermissionDenied;

		// Показываем результат: либо «Камера запущена», либо текст ошибки от сервиса.
		// «??» — если сервис не вернул текст ошибки, подставляем свой запасной вариант
		StatusText = result.Success ? "Камера запущена" : result.ErrorMessage ?? "Не удалось запустить камеру";
	}

	// Переключение на другую камеру, выбранную в Picker
	private async Task ChangeCameraAsync(CameraOption camera)
	{
		// Если выбрана та же камера, что уже открыта — ничего не делаем.
		// Страховка на случай, когда сработает присваивание SelectedCamera при обновлении списка
		if (camera.Id == _cameraService.SelectedCameraId)
		{
			return;
		}

		// Информируем пользователя: идёт переключение (оно не мгновенное — камера переоткрывается)
		StatusText = "Переключение камеры...";

		// Просим сервис переключиться. Он внутри останавливает старую камеру и запускает новую
		var selected = await _cameraService.SelectCameraAsync(camera.Id);

		// Успешное переключение: камера работает
		if (selected)
		{
			// Камера снова работает — возвращаем кнопку «Остановить»
			IsRunning = true;

			// Убираем служебный статус
			StatusText = "Камера запущена";
		}
		else
		{
			// Не удалось (камера не отвечает или отключилась) — сообщаем об этом
			StatusText = "Не удалось переключить камеру";
		}
	}

	// Переносит список камер из сервиса в коллекцию, доступную UI
	private void RefreshCameraOptions()
	{
		// Включаем подавление переключения: сейчас мы меняем SelectedCamera не по воле пользователя,
		// а сами, чтобы просто отметить активную камеру в Picker. Переключать камеру тут не нужно
		_suppressCameraSelection = true;

		// Очищаем коллекцию: Picker не умеет сам подхватывать замену коллекции,
		// поэтому элементы удаляются поштучно
		CameraOptions.Clear();

		// Копируем камеры из сервиса в нашу коллекцию
		foreach (var option in _cameraService.CameraOptions)
		{
			// Добавляем по одной — ObservableCollection уведомляет UI на каждом добавлении
			CameraOptions.Add(option);
		}

		// Выбираем в Picker именно ту камеру, которую открыл сервис.
		// Если по идентификатору найти не удалось (расхождение списков) — берём первую доступную
		SelectedCamera = CameraOptions.FirstOrDefault(option => option.Id == _cameraService.SelectedCameraId)
			?? CameraOptions.FirstOrDefault();

		// Снимаем подавление: с этого момента смена выбора пользователем снова вызовет переключение
		_suppressCameraSelection = false;

		// Picker показываем только если камер больше одной.
		// При одной камере выбирать нечего, и лишний контрол только мешает
		IsCameraPickerVisible = CameraOptions.Count > 1;
	}

	// Обработчик события «пришёл кадр» из сервиса.
	// ВАЖНО: вызывается на фоновом потоке платформы (Android-однопоточный исполнитель или поток Windows),
	// а свойства привязки менять разрешено только в UI-потоке
	private void OnFrameReceived(object? sender, CameraFrame frame)
	{
		// Текущее время в миллисекундах. TickCount64 монотонен и не «прыгает» при переводе часов,
		// в отличие от DateTime.Now
		var now = Environment.TickCount64;

		// ТРОТТЛИНГ: событие приходит 15–30 раз в секунду, а менять свойство привязки
		// так часто бессмысленно и дорого. Два условия пропуска:
		//   1) с прошлого обновления прошло меньше 100 мс — ограничиваем частоту;
		//   2) геометрия равна прежней (record сравнивает по значениям) — уведомлять UI незачем
		if (now - _lastGeometryUpdate < 100 || FrameGeometry == frame.Geometry)
		{
			return;
		}

		// Запоминаем момент обновления
		_lastGeometryUpdate = now;

		// Переключаемся в UI-поток: только там можно присваивать свойства, на которые завязаны биндинги.
		// BeginInvokeOnMainThread (а не InvokeOnMainThread) не ждёт выполнения — обработчик кадров
		// не должен блокироваться на UI-потоке, иначе видео начнёт подтормаживать
		MainThread.BeginInvokeOnMainThread(() => FrameGeometry = frame.Geometry);
	}
}
