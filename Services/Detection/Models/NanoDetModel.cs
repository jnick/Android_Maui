// Подключаем математические структуры OpenCV
using System.Globalization;

using OpenCvSharp;

// Подключаем модуль DNN: в OpenCvSharp 4.x сетевые функции вынесены из Cv2 в CvDnn
using OpenCvSharp.Dnn;

// Подключаем геометрию интерфейса для DetectedObject
using Microsoft.Maui.Graphics;

// Подключаем наши модели данных
using MauiBluethooth.Models.Detection;

// Подключаем препроцессинг кадра, подавление дубликатов и названия классов COCO
using MauiBluethooth.Services.Detection.Helpers;
using MauiBluethooth.Services.Detection.Preprocessing;

// Размер для OpenCV пишем явно: в проекте есть ещё Size из Microsoft.Maui.Graphics,
// и без псевдонима компилятор не может выбрать нужный
using CvSize = OpenCvSharp.Size;

// Объявляем пространство имён для конкретных моделей распознавания
namespace MauiBluethooth.Services.Detection.Models;

// Детектор объектов NanoDet (версия 2022 года) поверх OpenCV DNN.
//
// ВНИМАНИЕ: эталонный nanodet.py из зоопарка нельзя копировать. Он рассчитан
// на старую выгрузку, где выходы идут вперемешку: срез preds[::2] должен был
// дать все три карты классов, а preds[1::2] — все три карты рамок. У модели
// 2022 года порядок другой: сначала ВСЕ карты классов, потом ВСЕ карты рамок.
// Поэтому срез preds[::2] на этой модели даёт cls8, cls16, bbox8 — то есть
// половину нужного и половину лишнего. Именно из-за этого «эталонный» декод
// на свежей модели молча отдаёт мусор вместо рамок
//
// Что делает сеть. Это anchor-free детектор: у него нет заранее заданных якорей,
// вместо них используются центры ячеек трёхмасштабной сетки. Для каждой ячейки сеть
// предсказывает уверенности 80 классов и четыре расстояния от центра ячейки
// до сторон рамки (слева, сверху, справа, снизу)
public sealed class NanoDetModel : IDetectionModel
{
	// Размер входа сети
	private const int InputSize = 416;

	// Шаги пирамиды: три масштаба
	private static readonly int[] Strides = [8, 16, 32];

	// Число ячеек в строке сетки для каждого масштаба: 416/8, 416/16, 416/32
	private static readonly int[] GridSizes = [52, 26, 13];

	// Сколько значений отдаёт сеть на распределение одной стороны рамки.
	// reg_max = 7 означает 8 гибин: 0, 1, 2 ... 7.
	// Настоящее расстояние получается взвешенной суммой этих гинов с вероятностями
	private const int RegMax = 7;

	// Число гинов: reg_max + 1
	private const int RegBins = RegMax + 1;

	// Сколько сторон у рамки
	private const int BoxSides = 4;

	// Число значений в карте рамок одной ячейки: 4 стороны × 8 гинов
	private const int RegValues = BoxSides * RegBins;

	// Сколько классов в наборе COCO
	private const int ClassCount = 80;

	// Порог подавления дубликатов из эталонной реализации NanoDet.
	// Здесь он больше, чем у YOLOX: у anchor-free детектора дубликаты
	// реже перекрываются сильно, и жёсткий порог вырезал бы соседние объекты
	private const float NmsThreshold = 0.6f;

	// Загруженная сеть
	private readonly Net _net;

	// Замеры последнего вызова Detect
	private readonly List<TimingStage> _stages = [];

	// Рамки-кандидаты текущего кадра
	private readonly List<DetectedObject> _candidates = [];

	// Номера классов кандидатов — нужны для подавления по классам
	private readonly List<int> _candidateClasses = [];

	// Конструктор модели: читает сеть
	public NanoDetModel(ModelDescriptor descriptor, string modelPath)
	{
		// Сохраняем описание модели
		Descriptor = descriptor;

		// Проверяем наличие файла: иначе сообщение OpenCV не будет содержать имени файла
		if (!File.Exists(modelPath))
		{
			// Явно называем путь
			throw new FileNotFoundException($"Не найден файл модели NanoDet: {modelPath}", modelPath);
		}

		// Загружаем сеть
		_net = CvDnn.ReadNetFromOnnx(modelPath)
			?? throw new InvalidOperationException("OpenCV вернул пустую сеть NanoDet.");

		// Считаем на CPU: DNN-бэкенды OpenCV в NuGet-пакет не входят,
		// доступен только встроенный
		_net.SetPreferableTarget(Target.CPU);
	}

	// Описание модели
	public ModelDescriptor Descriptor { get; }

	// Замеры последнего распознавания
	public IReadOnlyList<TimingStage> LastStages => _stages;

	// Распознаёт объекты в кадре
	public void Detect(Mat bgrFrame, float scoreThreshold, List<DetectedObject> results)
	{
		// Наполняем результат заново
		results.Clear();

		// Обнуляем замеры прошлого кадра
		_stages.Clear();

		// Момент старта подготовки входа
		var preprocessStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Вписываем кадр в квадрат 416×416 серыми полями
		using var box = Letterbox.Apply(bgrFrame, InputSize);

		// Вписывания НЕ достаточно: у NanoDet, в отличие от YuNet и YOLOX,
		// есть вычитание среднего и деление на среднеквадратичное отклонение.
		// Без этой нормализации точность падает заметно — сеть обучена на таких же
		// числах и «не узнаёт» вход, который видит.
		// Средние и отклонения — константы обучения конкретной модели,
		// менять их «на глаз» нельзя
		NormalizeChannels(
			box.Letterboxed,
			new Scalar(103.53, 116.28, 123.675),
			new Scalar(57.375, 57.12, 58.395));

		// Собираем вход сети.
		// scaleFactor = 1: нормализация уже выполнена вручную, повторно масштабировать нельзя.
		// swapRB = false: NanoDet обучен на BGR, перестановка каналов испортила бы вход.
		// ВАЖНО: по умолчанию у BlobFromImage стоит swapRB: true, поэтому указываем явно
		using var blob = CvDnn.BlobFromImage(
			box.Letterboxed, 1.0, new CvSize(InputSize, InputSize), default, swapRB: false, crop: false);

		// Записываем время подготовительного этапа
		_stages.Add(new TimingStage("Подготовка входа", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - preprocessStart));

		// Момент старта сетевого прохода
		var forwardStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Забираем все выходы сети.
		//
		// ВАЖНО, здесь нет «просто вызвать Forward() и получить всё сразу»:
		// в OpenCvSharp 4.x перегрузка Forward без имени возвращает ОДИН Mat.
		// Чтобы получить все выходы, нужно сначала узнать имена слоёв, завести
		// под них массив и передать оба в Forward.
		//
		// Имена выходов у этой модели — номера слоёв (792, 795, 814, 817, 836, 839).
		// Они не несут смысла и меняются при пересборке, поэтому опираться на них
		// нельзя: назначение каждого выхода определяется по его форме, см. SplitOutputs
		var outputNames = _net.GetUnconnectedOutLayersNames();
		var outputs = new Mat[outputNames.Length];

		// Каждый слот обязан быть объектом Mat, а не null: managed-обёртка Forward
		// проходит по массиву и читает CvPtr до нативного вызова, и null-слот упал бы
		// NullReferenceException внутри OpenCvSharp, а не в нашем декодере
		for (var i = 0; i < outputs.Length; i++)
		{
			outputs[i] = new Mat();
		}
		try
		{
			// Подаём подготовленный вход сети. Без SetInput прямой проход стартует
			// без входных данных и падает на первом же кадре
			_net.SetInput(blob);

			// Выполняем сетевой проход и получаем все выходы разом
			_net.Forward(outputs, outputNames.Select(static name => name ?? string.Empty));

			// Замеряем чистое время сети
			_stages.Add(new TimingStage("NanoDet: сеть", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - forwardStart));

			// Момент старта разбора
			var decodeStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

			// Очищаем рабочие буферы
			_candidates.Clear();
			_candidateClasses.Clear();

			// Раскладываем выходы по назначению. Три карты классов и три карты рамок,
			// каждая со своей формой, поэтому определяем их по размеру последнего измерения
			var classMaps = new List<Mat>(Strides.Length);
			var boxMaps = new List<Mat>(Strides.Length);
			SplitOutputs(outputs, classMaps, boxMaps);

			// Каждая карта — отдельный Mat и содержит РОВНО свои ячейки: у карты масштаба
			// 52² — 2704 ячейки, у 26² — 676, у 13² — 169. Поэтому разбираем каждую карту
			// своими локальными индексами от нуля, а не общим номером ячейки: общий номер
			// дошёл бы до значений, которых в маленькой карте просто нет, и упал бы
			// IndexOutOfRangeException при чтении
			//
			// Ячейки масштабов идут в списках подряд и в том же порядке, что и карты
			// (сначала крупный масштаб), но это совпадение для чтения не нужно —
			// каждая карта разбирается независимо

			// Разбираем масштабы
			for (var level = 0; level < Strides.Length; level++)
			{
				// Число ячеек у этого масштаба: 52², 26² или 13²
				var cells = GridSizes[level] * GridSizes[level];

				// Читаем обе карты как плоские массивы
				var cls = DnnOutput.AsFloats(classMaps[level], cells * ClassCount, $"cls_{Strides[level]}");
				var reg = DnnOutput.AsFloats(boxMaps[level], cells * RegValues, $"bbox_{Strides[level]}");

				// Шаг сетки этого масштаба
				var stride = Strides[level];

				// Число ячеек в строке
				var grid = GridSizes[level];

				// Разбираем каждую ячейку
				for (var cell = 0; cell < cells; cell++)
				{
					// Ищем самый уверенный класс ячейки.
					// Считаем вручную, а не через LINQ: ячеек 3549, классов 80,
					// это 283 тысячи сравнений на кадр, и аллокации тут недопустимы
					var bestClass = 0;
					var bestScore = 0f;

					// Начало строки классов этой ячейки в плоском массиве.
					// Индексы локальные — внутри карты текущего масштаба
					var clsRow = cell * ClassCount;

					// Перебираем все 80 классов
					for (var c = 0; c < ClassCount; c++)
					{
						// Берём уверенность очередного класса
						var score = cls[clsRow + c];

						// Если она больше лучшей найденной — запоминаем
						if (score > bestScore)
						{
							// Запоминаем номер класса
							bestClass = c;

							// И его уверенность
							bestScore = score;
						}
					}

					// Отсекаем неуверенные ячейки.
					// Здесь главный источник экономии: ячеек 3549, а объектов в кадре
					// единицы, поэтому подавляющих проверок выполняется всего несколько десятков
					if (bestScore < scoreThreshold)
					{
						// Ячейка пустая — следующая
						continue;
					}

					// Координаты ячейки в сетке
					var gx = cell % grid;
					var gy = cell / grid;

					// Опорная точка — ЦЕНТР ячейки, а не левый верхний угол, как в YuNet.
					// Формула со сдвигом 0,5·(stride − 1) взята из эталонного примера
					// и отвечает за то, чтобы центр ячейки попал ровно в середину её шага
					var anchorX = (gx * stride) + (0.5f * (stride - 1));
					var anchorY = (gy * stride) + (0.5f * (stride - 1));

					// Начало строки распределений этой ячейки локально в карте рамок
					var regRow = cell * RegValues;

					// Для каждой из четырёх сторон восстанавливаем расстояние от центра.
					// Структура строки: 8 значений левой, 8 верхней, 8 правой, 8 нижней —
					// то есть сначала идёт ВСЯ гистограмма одной стороны, потом следующей
					var left = anchorX - (DecodeDistance(reg, regRow + (0 * RegBins)) * stride);
					var top = anchorY - (DecodeDistance(reg, regRow + (1 * RegBins)) * stride);
					var right = anchorX + (DecodeDistance(reg, regRow + (2 * RegBins)) * stride);
					var bottom = anchorY + (DecodeDistance(reg, regRow + (3 * RegBins)) * stride);

					// Зажимаем рамку в пределы входа: у края картинки сеть любит
					// предсказывать расстояния за границу
					var clampedLeft = Math.Clamp(left, 0f, InputSize);
					var clampedTop = Math.Clamp(top, 0f, InputSize);
					var clampedRight = Math.Clamp(right, 0f, InputSize);
					var clampedBottom = Math.Clamp(bottom, 0f, InputSize);

					// Вырожденные рамки пропускаем
					if (clampedRight - clampedLeft < 1f || clampedBottom - clampedTop < 1f)
					{
						// Слишком маленькая рамка — не рисуем
						continue;
					}

					// Добавляем кандидата
					_candidates.Add(new DetectedObject(
						bounds: new RectF(clampedLeft, clampedTop, clampedRight - clampedLeft, clampedBottom - clampedTop),
						label: CocoClasses.GetName(bestClass),
						confidence: bestScore));

					// Запоминаем класс кандидата для подавления по классам
					_candidateClasses.Add(bestClass);
				}

				// Следующая карта перебирается со своей первой ячейки
			}

			// Подавляем дубликаты по классам.
			// Список классов обязателен: рамка «человек» не должна подавлять рамку
			// «велосипед», даже если они полностью совпали
			var kept = NonMaxSuppression.Suppress(
				ExtractBounds(_candidates),
				ExtractScores(_candidates),
				_candidateClasses,
				NmsThreshold,
				scoreThreshold);

			// Возвращаем оставшиеся рамки в координаты кадра.
			// Порядок «сначала подавление, потом обратное преобразование» тот же,
			// что в YuNet: так пересчёт координат выполняется для десятка рамок,
			// а не для тысяч отброшенных
			foreach (var index in kept)
			{
				// Берём рамку во вписывании
				var candidate = _candidates[index];

				// Возвращаем левый верхний угол в кадр
				var topLeft = box.Unproject(candidate.Bounds.X, candidate.Bounds.Y);

				// Возвращаем правый нижний угол в кадр
				var bottomRight = box.Unproject(candidate.Bounds.Right, candidate.Bounds.Bottom);

				// Собираем готовый объект для интерфейса
				results.Add(new DetectedObject(
					bounds: new RectF(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y),
					label: candidate.Label,
					confidence: candidate.Confidence));
			}

			// Замеряем разбор выходов
			_stages.Add(new TimingStage("NanoDet: разбор", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - decodeStart));
		}
		finally
		{
			// Освобождаем выходы сети в любом случае, даже если разбор упал.
			// Каждый Mat держит нативную память, а их тут шесть
			foreach (var output in outputs)
			{
				// Отдельный выход может остаться неинициализированным,
				// если сеть вернула не все, поэтому проверяем на null
				output?.Dispose();
			}
		}
	}

	// Нормализует изображение по формуле (значение − среднее) / отклонение,
	// отдельно для каждого канала.
	//
	// ПОЧЕМУ ТАК СЛОЖНО, А НЕ convertTo.
	// У Mat есть удобный метод ConvertTo(dst, type, alpha, beta), который
	// за один проход делает «умножить на alpha и прибавить beta». Но alpha и beta
	// у него — ОДНИ числа на весь кадр, а у нас средние и отклонения свои
	// для каждого канала: у синего канала отклонение 57,375, у красного 57,12.
	// Одним числом такое не выразить.
	//
	// Поэтому разбиваем картинку на три канала, нормализуем каждый отдельно
	// (вот тут alpha и beta уже свои) и собираем обратно. Это три прохода
	// вместо одного, но на кадре 416×416 это доли миллисекунды
	private static void NormalizeChannels(Mat image, Scalar mean, Scalar std)
	{
		// Разбиваем трёхканальную картинку на три одноканальные.
		// Разбиение здесь только организационное: копий пикселей не делается,
		// каналы лишь «смотрят» на те же данные
		Cv2.Split(image, out var channels);

		try
		{
			// Нормализуем каждый канал своим коэффициентом
			for (var channel = 0; channel < channels.Length; channel++)
			{
				// Раскрываем формулу: (x − mean) / std = x·(1 / std) + (− mean / std).
				// Именно в таком виде она ложится в alpha и beta метода ConvertTo.
				// Здесь же происходит смена типа: 8-битные целые 0..255
				// превращаются в 32-битные дробные — сеть ждёт именно float
				channels[channel].ConvertTo(
					channels[channel],
					MatType.CV_32FC1,
					alpha: 1.0 / std[channel],
					beta: -mean[channel] / std[channel]);
			}

			// Собираем нормализованные каналы обратно в трёхканальную картинку
			Cv2.Merge(channels, image);
		}
		finally
		{
			// Каналы — это временные Mat, их нужно освободить в любом случае.
			// Пиксели каналов лежат в памяти самой картинки, так что здесь
			// освобождаются только обёртки
			foreach (var channel in channels)
			{
				// Освобождаем обёртку канала
				channel.Dispose();
			}
		}
	}

	// Восстанавливает расстояние от центра ячейки до одной стороны рамки
	// из восьми вероятностей, которые выдала сеть.
	//
	// Идея такая. Сеть не выдаёт расстояние числом — слишком неудобно для обучения
	// (все значения разной величины в одной карте). Вместо этого она выдаёт
	// распределение по 8 гинам: «расстояние скорее 3, чем 4». Мы переводим
	// распределение обратно в число взвешенным средним: складываем каждый гин,
	// умноженный на его вероятность
	private static float DecodeDistance(ReadOnlySpan<float> reg, int row)
	{
		// Считаем сумму вероятностей: они должны дать единицу, но из-за
		// вычислений с плавающей точкой сумма может отличаться на доли процента.
		// Нормируем на неё, иначе расстояния будут чуть «поплывшими»
		var sum = 0f;
		for (var bin = 0; bin < RegBins; bin++)
		{
			// Прибавляем вероятность очередного гина
			sum += reg[row + bin];
		}

		// Вырожденный случай: сеть не смогла распределить вероятности.
		// Возвращаем ноль — это лучше, чем деление на ноль и NaN во всех координатах
		if (sum <= 0f)
		{
			// Нет распределения — нет расстояния
			return 0f;
		}

		// Считаем взвешенную сумму гинов
		var distance = 0f;
		for (var bin = 0; bin < RegBins; bin++)
		{
			// Вклад гина: его номер, умноженный на его вероятность
			distance += bin * (reg[row + bin] / sum);
		}

		// Возвращаем расстояние в гинах
		return distance;
	}

	// Раскладывает шесть выходов сети на карты классов и карты рамок.
	//
	// Порядок выходов у NanoDet можно угадать, но можно и проверить.
	// У карты классов последнее измерение равно 80 (число классов COCO),
	// у карты рамок — 32 (4 стороны × 8 гинов). Эти два числа не пересекаются,
	// поэтому назначение выхода определяется однозначно и не зависит ни от имён
	// слоёв, ни от их порядка
	private static void SplitOutputs(Mat[] outputs, List<Mat> classMaps, List<Mat> boxMaps)
	{
		// Ждём ровно три карты каждого вида
		const int expectedMaps = 3;

		// Проходим по всем выходам сети
		foreach (var output in outputs)
		{
			// Определяем размер последнего измерения: у трёхмерного выхода это число
			// признаков, у двумерного — число каналов.
			// ВНИМАНИЕ: у Mat метод Size(), а не свойство. Для одного измерения
			// есть отдельная перегрузка Size(int dim) — она и нужна:
			// Size() без скобок даст «группу методов», а не число
			var features = output.Size(output.Dims - 1);

			// Карта классов: 80 признаков на ячейку
			if (features == ClassCount)
			{
				// Добавляем в список карт классов
				classMaps.Add(output);
			}
			// Карта рамок: 32 признака на ячейку
			else if (features == RegValues)
			{
				// Добавляем в список карт рамок
				boxMaps.Add(output);
			}
			// Неожиданный размер: это уже не та модель
			else
			{
				// Сообщаем размер — по нему сразу понятно, что скачано не то
				throw new InvalidOperationException(
					$"Выход NanoDet имеет последнее измерение {features}, " +
					$"а ожидалось {ClassCount} (классы) или {RegValues} (рамки). " +
					"Скорее всего, скачана другая версия модели.");
			}
		}

		// Проверяем, что карт ровно столько, сколько масштабов.
		// Без этой проверки при нехватке карт мы бы упали позже, с невнятной ошибкой
		// «индекс вышел за границы», от которой нельзя понять, что не так с моделью
		if (classMaps.Count != expectedMaps || boxMaps.Count != expectedMaps)
		{
			// Показываем, что получили
			throw new InvalidOperationException(
				$"Ожидалось по {expectedMaps} карты классов и рамок у NanoDet, " +
				$"а получено {classMaps.Count} и {boxMaps.Count}. " +
				"Этот декодер написан под версию 2022 года с reg_max = 7.");
		}
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

	// Освобождаем нативную память сети
	public void Dispose() => _net.Dispose();
}
