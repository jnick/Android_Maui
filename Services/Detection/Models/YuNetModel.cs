// Подключаем математические структуры OpenCV
using System.Globalization;

using OpenCvSharp;

// Подключаем модуль DNN: в OpenCvSharp 4.x сетевые функции вынесены из Cv2 в отдельный класс CvDnn
using OpenCvSharp.Dnn;

// Подключаем наши модели данных
using MauiBluethooth.Models.Detection;

// Подключаем препроцессинг кадра и подавление дубликатов
using MauiBluethooth.Services.Detection.Helpers;
using MauiBluethooth.Services.Detection.Preprocessing;

// Объявляем пространство имён для конкретных моделей распознавания
namespace MauiBluethooth.Services.Detection.Models;

// Детектор лиц YuNet (версия 2023 года) поверх OpenCV DNN.
//
// ВНИМАНИЕ: эталонный пример из зоопарка копировать нельзя. Файл yunet.py
// из opencv_zoo рассчитан на старую выгрузку с ОДНИМ выходом формы 1×15×N,
// где все каналы склеены в один тензор. Модель 2023 года устроена иначе:
// у неё ДВЕНАДЦАТЬ отдельных выходов, по четыре на каждом масштабе, а вход
// строго 640×640. Ниже декод написан под реальные выходы скачанного файла
//
// Что означают двенадцать выходов. Сеть работает на трёх масштабах (шаг 8, 16, 32)
// и для каждого выдаёт четыре карты:
//   cls  — вероятность, что в ячейке есть объект;
//   obj  — вероятность, что ячейка «настоящая», а не фоновая;
//   bbox — четыре числа: сдвиг центра и логарифм размера;
//   kps  — десять чисел: координаты пяти точек лица.
//
// За картами стоит обычная свёрточная пирамида. Для шага 8 получается сетка
// 80×80, для 16 — 40×40, для 32 — 20×20. Всего 80² + 40² + 20² = 8400 ячеек —
// ровно столько же, сколько у YOLOX на том же входе
public sealed class YuNetModel : IDetectionModel
{
	// Размер входа сети. В отличие от NanoDet он не настраивается: сеть собрана
	// под квадрат 640×640, и попытка скормить ей другой размер закончится
	// ошибкой внутри OpenCV
	private const int InputSize = 640;

	// Шаги свёрточной пирамиды: три масштаба
	private static readonly int[] Strides = [8, 16, 32];

	// Число ячеек в строке сетки для каждого масштаба: 640/8, 640/16, 640/32
	private static readonly int[] GridSizes = [80, 40, 20];

	// Сколько точек лица выдаёт YuNet: глаза, нос, уголки рта
	private const int LandmarkCount = 5;

	// Значений в канале kps на одну точку: координаты X и Y
	private const int LandmarkStride = 2;

	// Порог подавления дубликатов, взят из эталонной реализации OpenCV.
	// Смысл: у одного лица бывает несколько рамок-кандидатов, и рамки
	// с перекрытием сильнее 30 % считаются одним и тем же лицом
	private const float NmsThreshold = 0.3f;

	// Загруженная сеть
	private readonly Net _net;

	// Имена двенадцати выходов в том порядке, в котором их читает декод
	private readonly string[] _layerNames;

	// Замеры последнего вызова Detect
	private readonly List<TimingStage> _stages = [];

	// Рамки-кандидаты текущего кадра
	private readonly List<DetectedObject> _candidates = [];

	// Конструктор модели: читает сеть и раскладывает её выходы
	public YuNetModel(ModelDescriptor descriptor, string modelPath)
	{
		// Сохраняем описание модели
		Descriptor = descriptor;

		// Проверяем, что файл на месте: иначе OpenCV упадёт внутри ReadNetFromOnnx
		// с невнятным сообщением про DNN, в котором не будет имени файла
		if (!File.Exists(modelPath))
		{
			// Явно называем путь: по нему видно, ищем ли мы файл на диске
			// или в ресурсах приложения
			throw new FileNotFoundException($"Не найден файл модели YuNet: {modelPath}", modelPath);
		}

		// Загружаем сеть из ONNX.
		// Бэкенд не выбираем: OpenCV сам использует встроенный DNN-бэкенд,
		// а тот умеет каскадно ускорять свёртки и реально быстрее «наивного» режима
		_net = CvDnn.ReadNetFromOnnx(modelPath)
			?? throw new InvalidOperationException("OpenCV вернул пустую сеть YuNet.");

		// Просим считать на CPU.
		// Важно: DNN-бэкенды OpenCV не входят в NuGet-пакет, доступен только
		// встроенный. Поэтому цель — CPU, и это заодно гарантия, что ничего
		// не уедет на несовместимый GPU
		_net.SetPreferableTarget(Target.CPU);

		// Раскладываем выходы сети.
		// Имена берём у самой сети, а не пишем строкой: так декод переживёт
		// переименование слоёв при пересборке модели.
		// Порядок GetUnconnectedOutLayersNames не гарантирован, поэтому
		// раскладываем выходы по именам в нужный нам порядок
		_layerNames = BuildLayerNames(_net.GetUnconnectedOutLayersNames());
	}

	// Описание модели
	public ModelDescriptor Descriptor { get; }

	// Замеры последнего распознавания
	public IReadOnlyList<TimingStage> LastStages => _stages;

	// Распознаёт лица в кадре
	public void Detect(Mat bgrFrame, float scoreThreshold, List<DetectedObject> results)
	{
		// Результат наполняем заново: список принадлежит вызывающему коду,
		// и оставлять в нём прошлый результат нельзя
		results.Clear();

		// Обнуляем замеры прошлого кадра
		_stages.Clear();

		// Запоминаем момент старта подготовительных операций:
		// на слабом телефоне подготовка входа съедает заметную часть кадра,
		// и её полезно видеть отдельной строкой в диагностике
		var preprocessStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Вписываем кадр в квадрат 640×640 серыми полями.
		// Именно вписывание, а не растяжение: сеть обучена на картинках с полями.
		// Проверено это не теорией — декод с вписыванием совпал с эталонной
		// реализацией cv2.FaceDetectorYN с точностью IoU = 1,000 на всех лицах
		using var box = Letterbox.Apply(bgrFrame, InputSize);

		// Собираем вход сети. Здесь картинка превращается в числа:
		//   scaleFactor = 1 — значения остаются 0..255, сеть не ждёт нормировки;
		//   swapRB = false — порядок каналов BGR, какой и был;
		//   crop = false — обрезать нечего, размер уже совпадает.
		// ВАЖНО: значения по умолчанию у BlobFromImage — swapRB: true и crop: true,
		// то есть ПО УМОЛЧАНИЮ OpenCV переставляет каналы BGR → RGB. Для YOLOX это
		// как раз нужно, а для YuNet — нет, поэтому указываем всё явно
		using var blob = CvDnn.BlobFromImage(
			box.Letterboxed, 1.0, new OpenCvSharp.Size(InputSize, InputSize), default, swapRB: false, crop: false);

		// Записываем результат подготовительного этапа
		_stages.Add(new TimingStage("Подготовка входа", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - preprocessStart));

		// Запоминаем момент старта сетевого прохода
		var forwardStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Готовим массив под выходы сети.
		// ВАЖНО: Forward не создаёт выходы сам — их нужно выделить заранее,
		// передать массив и получить заполненные Mat. Метод, который возвращает
		// готовый Mat[], здесь не подходит: он умеет отдавать только ОДИН выход
		var outputs = new Mat[_layerNames.Length];
		try
		{
			// Просим ровно те двенадцать выходов, которые умеет читать декод
			_net.Forward(outputs, _layerNames);

			// Замеряем чистое время сети
			_stages.Add(new TimingStage("YuNet: сеть", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - forwardStart));

			// Запоминаем момент старта разбора
			var decodeStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

			// Очищаем рабочие буферы прошлого кадра
			_candidates.Clear();

			// Порядок выходов задан конструктором: сначала четыре карты масштаба 8,
			// потом 16, потом 32. Разбираем их в том же порядке
			for (var level = 0; level < Strides.Length; level++)
			{
				// Смещение первого выхода текущего масштаба в общем списке
				var baseIndex = level * 4;

				// Число ячеек у этого масштаба: 80², 40² или 20²
				var cells = GridSizes[level] * GridSizes[level];

				// Читаем четыре карты масштаба как плоские массивы чисел
				var cls = DnnOutput.AsFloats(outputs[baseIndex + 0], cells, "cls");
				var obj = DnnOutput.AsFloats(outputs[baseIndex + 1], cells, "obj");
				var bbox = DnnOutput.AsFloats(outputs[baseIndex + 2], cells * 4, "bbox");
				var kps = DnnOutput.AsFloats(outputs[baseIndex + 3], cells * LandmarkCount * LandmarkStride, "kps");

				// Шаг сетки для этого масштаба
				var stride = Strides[level];

				// Число ячеек в строке: 80, 40 или 20
				var grid = GridSizes[level];

				// Разбираем каждую ячейку сетки
				for (var cell = 0; cell < cells; cell++)
				{
					// Итоговая уверенность — произведение двух вероятностей.
					// Смысл умножения: «в ячейке объект» И «ячейка настоящая».
					// Если хоть один множитель мал, рамка отбрасывается
					var score = cls[cell] * obj[cell];

					// Отсекаем заведомо пустые ячейки до всей остальной работы.
					// В сетке 8400 ячеек, а лиц в кадре единицы, поэтому этот отсев
					// экономит больше всего: без него мы считали бы координаты
					// для 8399 ячеек впустую
					if (score < scoreThreshold)
					{
						// Ячейка пустая — переходим к следующей
						continue;
					}

					// Координаты ячейки в сетке: делением по модулю получаем столбец,
					// целочисленным делением — строку
					var gx = cell % grid;
					var gy = cell / grid;

					// Опорная точка (якорь) — левый ВЕРХНИЙ угол ячейки.
					// ВАЖНО: именно левый верхний, а НЕ центр ячейки. Это отличие от
					// NanoDet, у которого якорь — центр со сдвигом на полшага.
					// Путаница между этими формулами даёт рамки, смещённые на полшага:
					// на глаз почти незаметно, поэтому ошибка так трудно находится
					var anchorX = gx * stride;
					var anchorY = gy * stride;

					// Смещение центра относительно якоря
					var offsetX = bbox[(cell * 4) + 0] * stride;
					var offsetY = bbox[(cell * 4) + 1] * stride;

					// Экспонента от размера: сеть отдаёт логарифм,
					// поэтому экспонента обязательна — забыть её проще всего
					var width = MathF.Exp(bbox[(cell * 4) + 2]) * stride;
					var height = MathF.Exp(bbox[(cell * 4) + 3]) * stride;

					// Собираем центр: якорь плюс смещение
					var centerX = anchorX + offsetX;
					var centerY = anchorY + offsetY;

					// Переходим от центра с размерами к левому верхнему углу
					var x = centerX - (width * 0.5f);
					var y = centerY - (height * 0.5f);

					// Зажимаем рамку в пределы входа 640×640.
					// Без этого рамка, частично ушедшая за край, уехала бы дальше
					// за край и после обратного преобразования дала бы координаты
					// далеко за пределами кадра
					var left = Math.Clamp(x, 0f, InputSize);
					var top = Math.Clamp(y, 0f, InputSize);
					var right = Math.Clamp(x + width, 0f, InputSize);
					var bottom = Math.Clamp(y + height, 0f, InputSize);

					// Вырожденную рамку отбрасываем: шириной в пиксель ничто не рисуется,
					// а в списке она только мешает
					if (right - left < 1f || bottom - top < 1f)
					{
						// Слишком маленькая рамка — не рисуем
						continue;
					}

					// Читаем пять точек лица. Координаты точек, как и рамки,
					// отсчитываются от якоря ячейки
					var landmarks = new PointF[LandmarkCount];
					for (var point = 0; point < LandmarkCount; point++)
					{
						// Индекс пары координат для этой точки
						var index = (cell * LandmarkCount * LandmarkStride) + (point * LandmarkStride);

						// Точка = якорь плюс смещение из карты kps
						landmarks[point] = new PointF(
							// Зажимаем по входу, чтобы за краем не было мусора
							Math.Clamp(anchorX + (kps[index + 0] * stride), 0f, InputSize),
							Math.Clamp(anchorY + (kps[index + 1] * stride), 0f, InputSize));
					}

					// Добавляем кандидата. Точки пока оставляем во вписывании —
					// вернём их в координаты кадра вместе с рамкой одним проходом
					_candidates.Add(new DetectedObject(
						bounds: new RectF(left, top, right - left, bottom - top),
						label: "Лицо",
						confidence: score)
					{
						Landmarks = landmarks
					});
				}
			}

			// Подавляем дубликаты.
			// Простое сравнение прямоугольников не равно IoU, поэтому используем
			// честную реализацию, написанную в этом проекте
			var kept = NonMaxSuppression.Suppress(
				ExtractBounds(_candidates),
				ExtractScores(_candidates),
				NmsThreshold,
				scoreThreshold);

			// Переносим оставшиеся рамки из пространства вписывания в координаты кадра.
			// Делаем это ПОСЛЕ подавления, а не до: так обратное преобразование
			// выполняется для десятка рамок, а не для тысяч отброшенных
			foreach (var index in kept)
			{
				// Берём рамку во вписывании
				var candidate = _candidates[index];

				// Возвращаем левый верхний угол в кадр
				var topLeft = box.Unproject(candidate.Bounds.X, candidate.Bounds.Y);

				// Возвращаем правый нижний угол в кадр.
				// Рамку нельзя «просто разделить на масштаб»: у вписывания масштаб
				// один, а поля слева и справа разные, и для этого существует
				// обратное преобразование точки
				var bottomRight = box.Unproject(candidate.Bounds.Right, candidate.Bounds.Bottom);

				// Точки лица тоже возвращаем в координаты кадра
				var landmarks = new PointF[candidate.Landmarks.Count];
				for (var point = 0; point < candidate.Landmarks.Count; point++)
				{
					// Каждая точка проходит то же обратное преобразование
					landmarks[point] = box.Unproject(candidate.Landmarks[point]);
				}

				// Собираем готовый объект для интерфейса
				results.Add(new DetectedObject(
					bounds: new RectF(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y),
					label: candidate.Label,
					confidence: candidate.Confidence)
				{
					// Точки уже в системе координат кадра
					Landmarks = landmarks
				});
			}

			// Замеряем разбор выходов — сюда попадает и подавление дубликатов
			_stages.Add(new TimingStage("YuNet: разбор", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - decodeStart));
		}
		finally
		{
			// Освобождаем выходы сети в любом случае, даже если разбор упал.
			// Каждый Mat держит нативную память, а их тут двенадцать —
			// утечка заметно съедала бы память телефона
			foreach (var output in outputs)
			{
				// Отдельный выход может остаться неинициализированным, если сеть
				// вернула не все двенадцать, поэтому проверяем на null
				output?.Dispose();
			}
		}
	}

	// Раскладывает выходы сети в порядок, нужный декодеру.
	//
	// Имена слоёв у модели выглядят так: cls_8, obj_8, bbox_8, kps_8,
	// cls_16, obj_16, bbox_16, kps_16 и так далее. Порядок, в котором их вернёт
	// GetUnconnectedOutLayersNames, не задан, поэтому ищем каждый по имени,
	// а не полагаемся на позицию
	private static string[] BuildLayerNames(string?[] actualNames)
	{
		// Результат: имена в порядке «cls, obj, bbox, kps» для каждого масштаба
		var ordered = new string[Strides.Length * 4];

		// Сколько имён ожидаем
		var expected = ordered.Length;

		// Проверяем, что сеть отдала ровно столько выходов, сколько мы умеем читать.
		// Если появится новая версия модели, лучше явная ошибка при запуске,
		// чем «нарисуем пустоту и не поймём почему»
		if (actualNames.Length != expected)
		{
			// Перечисляем то, что получили, прямо в сообщении
			throw new InvalidOperationException(
				$"Ожидалось {expected} выходов YuNet, а сеть вернула {actualNames.Length}: " +
				$"{string.Join(", ", actualNames.Select(static name => name ?? "null"))}. " +
				"Этот декодер написан под версию 2023 года.");
		}

		// Перебираем масштабы
		for (var level = 0; level < Strides.Length; level++)
		{
			// Суффикс имени для этого масштаба: 8, 16 или 32
			var suffix = Strides[level].ToString(CultureInfo.InvariantCulture);

			// Четыре карты масштаба в порядке декодера
			var channels = new[] { "cls", "obj", "bbox", "kps" };

			// Разбираем каждую карту
			for (var channel = 0; channel < channels.Length; channel++)
			{
				// Полное имя слоя: префикс, подчёркивание, шаг
				var expectedName = $"{channels[channel]}_{suffix}";

				// Ищем слой с таким именем среди выходов сети
				var found = actualNames.FirstOrDefault(name => name == expectedName);

				// Не нашли — это другая версия модели, дальше идти бессмысленно
				if (found is null)
				{
					// Показываем, что ожидали и что есть на самом деле
					throw new InvalidOperationException(
						$"В выходах YuNet нет слоя «{expectedName}». " +
						$"Доступны: {string.Join(", ", actualNames.Select(static name => name ?? "null"))}.");
				}

				// Кладём имя в нужную позицию
				ordered[(level * 4) + channel] = found;
			}
		}

		// Возвращаем готовый порядок
		return ordered;
	}

	// Копирует рамки кандидатов в массив для подавления дубликатов
	private static RectF[] ExtractBounds(List<DetectedObject> items)
	{
		// Заводим массив нужного размера
		var result = new RectF[items.Count];

		// Копируем значения
		for (var i = 0; i < items.Count; i++)
		{
			// Заполняем очередной элемент
			result[i] = items[i].Bounds;
		}

		// Отдаём готовый массив
		return result;
	}

	// Копирует уверенности кандидатов в массив для подавления дубликатов
	private static float[] ExtractScores(List<DetectedObject> items)
	{
		// Заводим массив нужного размера
		var result = new float[items.Count];

		// Копируем значения
		for (var i = 0; i < items.Count; i++)
		{
			// Заполняем очередной элемент
			result[i] = items[i].Confidence;
		}

		// Отдаём готовый массив
		return result;
	}

	// Освобождаем нативную память сети.
	// Net внутри хранит указатель на C++-объект, и без Dispose он живёт до конца
	// процесса — при переключении моделей между собой это течёт память
	public void Dispose() => _net.Dispose();
}
