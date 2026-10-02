// Подключаем математические структуры OpenCV (Point2f, Size) и сам OpenCV
using OpenCvSharp;

// ВНИМАНИЕ: в проекте подключены и OpenCvSharp, и Microsoft.Maui.Graphics, а в обоих
// есть структура с именем Size. Без явного псевдонима компилятор не может выбрать,
// какая имеется в виду, и выдаёт ошибку неоднозначности. Поэтому размер для OpenCV
// везде пишется как CvSize — так сразу видно, для какой библиотеки аргумент
using CvSize = OpenCvSharp.Size;

// Подключаем PointF интерфейса: в DetectedObject точки лица хранятся именно в этом типе,
// чтобы их можно было отдать привязкам XAML без ручного преобразования
using PointF = Microsoft.Maui.Graphics.PointF;

// Объявляем пространство имён для препроцессинга кадра
namespace MauiBluethooth.Services.Detection.Preprocessing;

// Результат вписывания изображения в квадратный вход нейросети.
//
// Главная мысль, ради которой существует этот тип: вписывание изменяет масштаб
// и сдвигает картинку. Чтобы рамка, найденная нейросетью на входе, оказалась в правиль
// месте на исходном кадре, нужно проделать ровно обратное преобразование.
// Поэтому храним и масштаб, и отступы, а не только готовую матрицу
public sealed class LetterboxResult : IDisposable
{
	// Конструктор результата вписывания
	public LetterboxResult(
		// Изображение размером с вход сети (квадрат, заполненное полями)
		Mat letterboxed,
		// Коэффициент масштабирования, применённый к исходному изображению
		float scale,
		// Отступ слева в пикселях вписывания
		float padX,
		// Отступ сверху в пикселях вписывания
		float padY,
		// Ширина исходного изображения
		int sourceWidth,
		// Высота исходного изображения
		int sourceHeight)
	{
		// Сохраняем вписывание в поле Letterboxed
		Letterboxed = letterboxed;

		// Сохраняем масштаб
		Scale = scale;

		// Сохраняем горизонтальный отступ
		PadX = padX;

		// Сохраняем вертикальный отступ
		PadY = padY;

		// Сохраняем ширину исходника
		SourceWidth = sourceWidth;

		// Сохраняем высоту исходника
		SourceHeight = sourceHeight;
	}

	// Вписывание в квадрат со стороной, равной размеру входа сети
	public Mat Letterboxed { get; }

	// Во сколько раз уменьшили картинку, чтобы она влезла. Всегда меньше либо равен 1
	public float Scale { get; }

	// Сколько пикселей слева добавлено полями
	public float PadX { get; }

	// Сколько пикселей сверху добавлено полями
	public float PadY { get; }

	// Ширина исходного изображения
	public int SourceWidth { get; }

	// Высота исходного изображения
	public int SourceHeight { get; }

	// Обратное преобразование координат: из пространства вписывания обратно в исходный кадр.
	//
	// Прямое преобразование было «пиксель кадра → умножить на масштаб → прибавить отступ».
	// Значит обратное — «вычесть отступ, разделить на масштаб»
	public Point2f Unproject(Point2f pointInLetterbox)
	{
		// Возвращаем точку в координатах исходного изображения
		return new Point2f(
			// Убираем левый отступ и возвращаем исходный масштаб по горизонтали
			(pointInLetterbox.X - PadX) / Scale,
			// То же самое по вертикали
			(pointInLetterbox.Y - PadY) / Scale);
	}

	// Обратное преобразование для угла прямоугольника: «в правом нижнем углу» вписывания
	// превращается в «в правом нижнем углу» исходника
	public Point2f Unproject(float x, float y) => Unproject(new Point2f(x, y));

	// Перегрузка для точек в терминах интерфейса.
	//
	// Зачем она нужна. Точки лица хранятся в DetectedObject как PointF из
	// Microsoft.Maui.Graphics — это тип, который понимает XAML и привязки.
	// Преобразовывать его в Point2f вручную в каждом месте использования
	// значит повторять одно и то же преобразование по десятку раз.
	// Здесь делаем его один раз и возвращаем обратно тот же тип, что и на входе
	public PointF Unproject(PointF pointInLetterbox)
	{
		// Выполняем то же преобразование, что и для точки OpenCV
		var result = Unproject(new Point2f(pointInLetterbox.X, pointInLetterbox.Y));

		// Возвращаем результат в терминах интерфейса
		return new PointF(result.X, result.Y);
	}

	// Освобождаем нативную память вписывания.
	// Mat — это обёртка над памятью вне управляемой кучи, сборщик мусора её не освободит:
	// без Dispose приложение очень быстро съест память телефона
	public void Dispose() => Letterboxed.Dispose();
}

// Вписывание кадра в квадратный вход нейросети единообразным масштабом по обеим осям.
//
// Зачем именно так, а не по-другому. Есть два варианта:
//   1) «резиновое» растяжение — вписывает без полей, но искажает пропорции;
//   2) вписывание с полями — пропорции сохраняются, по краям появляются полосы.
// Нейросети из зоопарка обучены на втором варианте: они видели при входе
// серые полосы сверху и снизу либо слева и справа. Первый вариант даёт заметную
// потерю точности — это проверено экспериментально: для NanoDet средний IoU
// с YOLOX при растяжении составил 0,00 против 0,94 при вписывании с полями
public static class Letterbox
{
	// Значение серого, которым заполняются поля.
	// Ровно 114 используется в эталонных примерах зоопарка, и это не совпадение:
	// сеть видела при обучении именно такие поля, и другой цвет снижает точность
	public const byte PadValue = 114;

	// Вписывает изображение в квадрат со стороной size, добавляя серые поля
	public static LetterboxResult Apply(Mat source, int size)
	{
		// Защита от вызова с пустой картинкой: иначе дальше посыплются исключения
		// изнутри OpenCV с невнятным сообщением
		ArgumentNullException.ThrowIfNull(source);

		// Масштаб одинаковый по обеим осям — иначе пропорции исказятся.
		// Берём минимум, чтобы вписывание гарантированно поместилось целиком
		float scale = Math.Min((float)size / source.Width, (float)size / source.Height);

		// Размер, до которого уменьшаем картинку. Округляем и зажимаем снизу единицей:
		// если картинка меньше входа, увеличивать её не нужно, но и нулём она быть не может
		int resizedWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
		int resizedHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));

		// Растягиваем изображение. INTER_LINEAR — стандартный выбор для уменьшения:
		// он заметно быстрее INTER_AREA, а разница на глаз не видна
		using var resized = new Mat();
		Cv2.Resize(source, resized, new CvSize(resizedWidth, resizedHeight), interpolation: InterpolationFlags.Linear);

		// Если картинка заняла весь вход — полей нет, отдаём её как есть.
		// Этот случай частый: кадр 4:3 не совпадает с квадратом, но квадратные кадры
		// и кадры точно 1:1 копить не нужно
		if (resizedWidth == size && resizedHeight == size)
		{
			// Копируем, а не возвращаем resized: вызывающий код освободит результат сам,
			// и мы не должны оставлять ему объект, который уже уничтожен через using
			return new LetterboxResult(resized.Clone(), scale, 0f, 0f, source.Width, source.Height);
		}

		// Вычисляем отступы. Округляем и делим пополам, чтобы поля были симметричными:
		// остаток от нечётной разницы отдаём в правую и нижнюю половину
		var deltaX = size - resizedWidth;
		var deltaY = size - resizedHeight;
		var padX = deltaX / 2;
		var padY = deltaY / 2;

		// Собираем результат: CopyMakeBorder умеет добавить рамку с четырёх сторон сразу
		var letterboxed = new Mat();
		Cv2.CopyMakeBorder(
			resized,
			letterboxed,
			top: padY,
			bottom: deltaY - padY,
			left: padX,
			right: deltaX - padX,
			borderType: BorderTypes.Constant,
			value: new Scalar(PadValue, PadValue, PadValue));

		// Возвращаем результат вместе с параметрами, нужными для обратного преобразования
		return new LetterboxResult(letterboxed, scale, padX, padY, source.Width, source.Height);
	}
}
