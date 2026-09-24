// Подключаем пространство имён с атрибутами Android (Activity, LaunchMode, ConfigChanges)
using Android.App;
// Подключаем пространство имён с классами конфигурации манифеста (ConfigChanges, LaunchMode)
using Android.Content.PM;
// Подключаем пространство имён с классами операционной системы Android
using Android.OS;

// Объявляем корневое пространство имён приложения
namespace MauiBluethooth;

// Атрибут Activity: задаёт параметры стартового экрана (Activity) в манифесте:
// Theme — стиль заставки; MainLauncher — главная активность, запускаемая первой; 
// LaunchMode.SingleTop — не создавать второй экземпляр, если уже сверху; 
// ConfigurationChanges — список изменений конфигурации (поворот, режим, размер экрана), 
// после которых активность не пересоздаётся
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Главная активность MAUI приложения для Android
public class MainActivity : MauiAppCompatActivity
{
	// Тело класса пустое — вся логика в базовом классе MauiAppCompatActivity
}