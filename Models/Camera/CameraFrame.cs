// Объявляем пространство имён для моделей данных, связанных с камерой
namespace MauiBluethooth.Models.Camera;

// Перечисление форматов хранения пикселей в кадре.
// Порядок байт в пикселе различается между платформами, поэтому его нужно хранить явно
public enum CameraPixelFormat
{
	// Red-Green-Blue-Alpha: так отдаёт кадры Android (CameraX, RGBA_8888)
	Rgba,

	// Blue-Green-Red-Alpha: так отдаёт кадры Windows (MediaFrameReader, Bgra8)
	Bgra
}

// Геометрия кадра: размеры «сырых» пикселей, угол поворота и признак зеркальности.
// Вынесена отдельно от данных кадра, потому что она меняется гораздо реже самих пикселей
public sealed record CameraFrameGeometry
{
	// Конструктор геометрии: задаёт размеры кадра, угол поворота и зеркальность
	public CameraFrameGeometry(int width, int height, int rotationDegrees, bool isMirrored)
	{
		// Ширина кадра в пикселях (в «сырой» ориентации датчика, до поворота)
		Width = width;

		// Высота кадра в пикселях (в «сырой» ориентации датчика, до поворота)
		Height = height;

		// Приводим угол поворота к диапазону 0..359.
		// Формула ((x % 360) + 360) % 360 корректна и для отрицательных значений (например, -90 → 270)
		RotationDegrees = ((rotationDegrees % 360) + 360) % 360;

		// Признак зеркальности: у фронтальной камеры изображение обычно отражается по горизонтали
		IsMirrored = isMirrored;
	}

	// Ширина кадра в пикселях в исходной ориентации
	public int Width { get; }

	// Высота кадра в пикселях в исходной ориентации
	public int Height { get; }

	// Угол поворота изображения (0, 90, 180 или 270 градусов), нормализованный в диапазон 0..359
	public int RotationDegrees { get; }

	// Нужно ли отражать изображение по горизонтали при выводе на экран
	public bool IsMirrored { get; }

	// Ширина кадра ПОСЛЕ поворота на 90 или 270 градусов: ширина и высота меняются местами
	public int DisplayWidth => RotationDegrees is 90 or 270 ? Height : Width;

	// Высота кадра ПОСЛЕ поворота на 90 или 270 градусов: берём исходную ширину
	public int DisplayHeight => RotationDegrees is 90 or 270 ? Width : Height;
}

// Один кадр, полученный с камеры: пиксели + всё, что нужно для их интерпретации.
// Класс «sealed» — наследовать кадр бессмысленно, поэтому запрещаем наследование
public sealed class CameraFrame
{
	// Конструктор кадра: принимает пиксели и их описание
	public CameraFrame(
		// Сырые байты пикселей кадра
		ReadOnlyMemory<byte> data,
		// Ширина кадра в пикселях
		int width,
		// Высота кадра в пикселях
		int height,
		// Число байт в одной строке изображения (может быть больше, чем width * 4, из-за выравнивания)
		int stride,
		// Порядок байт в пикселе
		CameraPixelFormat pixelFormat,
		// Геометрия кадра (поворот, зеркальность)
		CameraFrameGeometry geometry,
		// Время получения кадра в миллисекундах с начала эпохи Unix
		long timestampMilliseconds)
	{
		// Сохраняем ссылку на буфер с пикселями
		Data = data;

		// Сохраняем ширину кадра
		Width = width;

		// Сохраняем высоту кадра
		Height = height;

		// Сохраняем размер строки (шаг) изображения
		Stride = stride;

		// Сохраняем формат пикселей
		PixelFormat = pixelFormat;

		// Сохраняем геометрию кадра
		Geometry = geometry;

		// Сохраняем метку времени кадра
		TimestampMilliseconds = timestampMilliseconds;
	}

	// Пиксели кадра. ReadOnlyMemory — доступ только для чтения, чтобы обработчик не испортил данные платформы
	public ReadOnlyMemory<byte> Data { get; }

	// Ширина кадра в пикселях
	public int Width { get; }

	// Высота кадра в пикселях
	public int Height { get; }

	// Шаг строки: сколько байт занимает одна строка изображения в памяти
	public int Stride { get; }

	// Формат пикселей кадра
	public CameraPixelFormat PixelFormat { get; }

	// Геометрия кадра: размеры, поворот, зеркальность
	public CameraFrameGeometry Geometry { get; }

	// Метка времени получения кадра (мс с начала эпохи Unix) — нужна для измерения FPS
	public long TimestampMilliseconds { get; }
}

// Результат попытки запустить камеру.
// record даёт бесплатно сравнение по значениям и удобное ToString для диагностики
public sealed record CameraStartResult(bool Success, string? ErrorMessage = null);
