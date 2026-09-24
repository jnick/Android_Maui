// Подключаем пространство имён Android-приложений (атрибут Application)
using Android.App;
// Подключаем пространство имён для управления JNI-маршалингом (JniHandleOwnership) и IntPtr
using Android.Runtime;

// Объявляем корневое пространство имён приложения
namespace MauiBluethooth;

// Атрибут [Application] — помечает класс как приложение Android (точка входа при старте ОС)
[Application]
// Класс приложения MAUI для Android (наследует MauiApplication — системная точка входа)
public class MainApplication : MauiApplication
{
	// Конструктор приложения: вызывается ОС при старте
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		// Передаём системные параметры (указатель на Java-объект и владение им) базовому классу
		: base(handle, ownership)
	{
	}

	// Переопределяем метод создания MAUI-приложения
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
	// Реализация — вызов общей точки сборки приложения из MauiProgram
}