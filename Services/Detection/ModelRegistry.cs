// Подключаем наши модели данных и сами модели распознавания
using MauiBluethooth.Models.Detection;
using MauiBluethooth.Services.Detection.Models;

// Объявляем пространство имён для сервисов распознавания
namespace MauiBluethooth.Services.Detection;

// Каталог доступных моделей распознавания и их загрузка.
//
// ЗАЧЕМ НУЖЕН ОТДЕЛЬНЫЙ КАТАЛОГ, А НЕ ПОИСК ПО ФАЙЛАМ.
// Файлы моделей лежат в ресурсах приложения, и их можно было бы перечислить
// при старте. Но тогда опечатка в имени файла не выявется до момента,
// когда пользователь выберет именно этот пункт, — то есть уже в работе.
// Каталог, перечисленный кодом, компилируется: переименуем файл в папке,
// и сборка упадёт сразу. Поэтому список живёт здесь, а файлы — в ресурсах
public sealed class ModelRegistry
{
	// Размер входа YuNet
	private const int YuNetInputSize = 640;

	// Размер входа NanoDet
	private const int NanoDetInputSize = 416;

	// Размер входа YOLOX
	private const int YoloXInputSize = 640;

	// Список всех доступных моделей.
	// Порядок здесь — это порядок в выпадающем списке интерфейса,
	// поэтому он продуман: сначала то, что работает быстрее всего
	private static readonly ModelDescriptor[] Catalogue =
	[
		// Лицо, плавающая точность. Точность выше, но на телефоне медленнее
		new(
			id: "yunet-fp32",
			displayName: "Лицо · YuNet · fp32",
			kind: DetectionModelKind.Face,
			inputSize: YuNetInputSize,
			assetName: "models/face_detection_yunet_2023mar.onnx"),

		// Лицо, 8 бит. В восемь раз меньше весит и заметно быстрее
		new(
			id: "yunet-int8",
			displayName: "Лицо · YuNet · int8",
			kind: DetectionModelKind.Face,
			inputSize: YuNetInputSize,
			assetName: "models/face_detection_yunet_2023mar_int8.onnx"),

		// Объекты, плавающая точность
		new(
			id: "nanodet-fp32",
			displayName: "Объекты · NanoDet · fp32",
			kind: DetectionModelKind.Objects,
			inputSize: NanoDetInputSize,
			assetName: "models/object_detection_nanodet_2022nov.onnx"),

		// Объекты, 8 бит
		new(
			id: "nanodet-int8",
			displayName: "Объекты · NanoDet · int8",
			kind: DetectionModelKind.Objects,
			inputSize: NanoDetInputSize,
			assetName: "models/object_detection_nanodet_2022nov_int8.onnx"),

		// Объекты, YOLOX в плавающей точке. Самая тяжёлая модель в списке
		new(
			id: "yolox-fp32",
			displayName: "Объекты · YOLOX · fp32",
			kind: DetectionModelKind.Objects,
			inputSize: YoloXInputSize,
			assetName: "models/object_detection_yolox_2022nov.onnx"),

		// Объекты, YOLOX в 8 битах
		new(
			id: "yolox-int8",
			displayName: "Объекты · YOLOX · int8",
			kind: DetectionModelKind.Objects,
			inputSize: YoloXInputSize,
			assetName: "models/object_detection_yolox_2022nov_int8.onnx"),

		// Геометрические фигуры. Не нейросеть, поэтому размера входа нет
		new(
			id: "shapes-classical",
			displayName: "Фигуры · классика · OpenCV",
			kind: DetectionModelKind.Shapes,
			inputSize: 0,
			assetName: "")
	];

	// Пути к уже распакованным файлам моделей на диске.
	// Нужен потому, что сеть не умеет читать модель прямо из памяти приложения:
	// OpenCV DNN требует путь к файлу. При каждом переключении модели файл
	// копируется заново, а это для YOLOX тридцать с лишним мегабайт
	private readonly Dictionary<string, string> _extractedPaths = [];

	// Все доступные модели в порядке показа в интерфейсе
	public IReadOnlyList<ModelDescriptor> Models => Catalogue;

	// Модель, выбранная по умолчанию.
	// Берём YuNet в 8 битах: она умеет находить лица, работает быстро
	// и почти не тратит память — с неё комфортно начать знакомство
	public ModelDescriptor DefaultModel { get; } = Catalogue[1];

	// Создаёт модель по её описанию.
	//
	// Модели загружаются лениво, по одной: читать все шесть сетей при старте
	// означало бы потратить несколько секунд и десятки мегабайт памяти
	// только для того, чтобы пользователь ими не воспользовался.
	// Загрузка самой большой модели занимает больше секунды, поэтому
	// вызывающий код обязан запускать её не в UI-потоке
	public async Task<IDetectionModel> CreateAsync(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
	{
		// Проверяем, что описание вообще из нашего каталога.
		// Модель, которой нет в каталоге, создавать нечем: нечего и загружать
		if (!Catalogue.Contains(descriptor))
		{
			// Явно называем неизвестный идентификатор
			throw new ArgumentException($"Модель «{descriptor.Id}» не найдена в каталоге.", nameof(descriptor));
		}

		// Классическому детектору фигур модель не нужна: у него нет ни сети, ни файла.
		// Он создаётся сразу и ничего не читает с диска
		if (descriptor.Kind == DetectionModelKind.Shapes)
		{
			// Возвращаем готовый детектор фигур
			return new ClassicalShapeModel(descriptor);
		}

		// Достаём файл модели на диск, причём не каждый раз: результат запоминаем
		var modelPath = await ExtractModelAsync(descriptor.AssetName, cancellationToken);

		// Создаём нужную реализацию. Выбор идёт по идентификатору семейства,
		// а не по размеру входа — размер у NanoDet и YOLOX отличается,
		// а вот имя файла надёжнее
		return descriptor.AssetName.Contains("yunet", StringComparison.OrdinalIgnoreCase)
			? new YuNetModel(descriptor, modelPath)
			: descriptor.AssetName.Contains("nanodet", StringComparison.OrdinalIgnoreCase)
				? new NanoDetModel(descriptor, modelPath)
				: new YoloXModel(descriptor, modelPath);
	}

	// Достаёт файл модели из ресурсов приложения на диск и возвращает путь.
	//
	// ЗАЧЕМ ЭТО НУЖНО. OpenCV DNN умеет читать модель из файла и из массива байт,
	// но только для части форматов. Для ONNX-файлов из зоопарка надёжный путь —
	// настоящий файл на диске. Под Android и Windows файлы ресурсов лежат внутри
	// пакета, и платформа сама распаковывает их во временную папку при обращении
	private async Task<string> ExtractModelAsync(string assetName, CancellationToken cancellationToken)
	{
		// Запоминаем, что модель не выбрана, а не описываем пустой путь:
		// пустой путь — это валидное значение, его легко перепутать с ошибкой
		if (string.IsNullOrEmpty(assetName))
		{
			// Пустое имя ресурса — это ошибка в каталоге, а не в данных
			throw new ArgumentException("У модели не задано имя файла.", nameof(assetName));
		}

		// Проверяем кеш: возможно, этот файл мы уже доставали
		if (_extractedPaths.TryGetValue(assetName, out var cached))
		{
			// Файл на месте и путь известен — возвращаем сразу
			return cached;
		}

		// Просим платформу распаковать ресурс во временный файл.
		// На Android это assets/models/x.onnx, на Windows — папка рядом
		// с исполняемым файлом. Путь, который подходит обеим платформам:
		// он указывает на файл внутри пакета приложения
		await using var stream = await FileSystem.OpenAppPackageFileAsync(assetName);

		// Распаковываем во временный файл с понятным именем.
		// Имя сохраняем осмысленным, а не случайным: в сообщениях об ошибках
		// путь к файлу — первое, на что смотрят
		var temporaryPath = Path.Combine(
			Path.GetTempPath(),
			$"mauibluethooth_{Path.GetFileName(assetName)}");

		// Пишем содержимое в файл.
		// Копирование нужно, потому что OpenCV откроет файл по пути позже,
		// когда поток из OpenAppPackageFileAsync уже будет закрыт
		await using (var file = File.Create(temporaryPath))
		{
			// Переносим содержимое потока в файл
			await stream.CopyToAsync(file, cancellationToken);
		}

		// Запоминаем путь, чтобы в следующий раз не распаковывать заново
		_extractedPaths[assetName] = temporaryPath;

		// Возвращаем путь к готовому файлу
		return temporaryPath;
	}
}
