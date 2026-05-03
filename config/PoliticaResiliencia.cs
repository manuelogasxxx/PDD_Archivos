using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace PDD_Archivos.Configuracion;

public static class PoliticaResiliencia
{
   
    public static ResiliencePipeline Construir(
        ConfiguracionRabbitMq configuracion,
        Action<string> registrarMensaje)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(ConstruirOpcionesReintento(configuracion, registrarMensaje))
            .AddCircuitBreaker(ConstruirOpcionesCircuito(configuracion, registrarMensaje))
            .Build();
    }


    private static RetryStrategyOptions ConstruirOpcionesReintento(
        ConfiguracionRabbitMq configuracion,
        Action<string> registrarMensaje)
    {
        return new RetryStrategyOptions
        {
            // Número máximo de reintentos antes de rendirse
            MaxRetryAttempts = configuracion.MaximoReintentos,
            Delay       = TimeSpan.FromSeconds(configuracion.SegundosEsperaInicial),
            BackoffType = DelayBackoffType.Exponential,

            UseJitter = true,

            // Solo reintentar en errores de comunicación con la cola
            ShouldHandle = new PredicateBuilder()
                .Handle<RabbitMQ.Client.Exceptions.BrokerUnreachableException>()
                .Handle<RabbitMQ.Client.Exceptions.AlreadyClosedException>()
                .Handle<System.IO.IOException>()
                .Handle<TimeoutException>(),

            // Se ejecuta antes de cada reintento para informar el estado
            OnRetry = argumentos =>
            {
                registrarMensaje(
                    $"  [Reintento] Intento {argumentos.AttemptNumber + 1}/" +
                    $"{configuracion.MaximoReintentos} fallido. " +
                    $"Reintentando en {argumentos.RetryDelay.TotalSeconds:F1}s... " +
                    $"Error: {argumentos.Outcome.Exception?.Message}");
                return ValueTask.CompletedTask;
            }
        };
    }

    private static CircuitBreakerStrategyOptions ConstruirOpcionesCircuito(
        ConfiguracionRabbitMq configuracion,
        Action<string> registrarMensaje)
    {
        return new CircuitBreakerStrategyOptions
        {
            
            
            FailureRatio      = 0.6, // Abrir el circuito cuando el 60% de los últimos intentos fallen
            SamplingDuration  = TimeSpan.FromSeconds(30), 
            MinimumThroughput = 3, //Necesita al menos 3 intentos

            // Tiempo que el circuito permanece abierto antes de probar de nuevo
            BreakDuration = TimeSpan.FromSeconds(configuracion.SegundosCircuitoAbierto),

            ShouldHandle = new PredicateBuilder()
                .Handle<RabbitMQ.Client.Exceptions.BrokerUnreachableException>()
                .Handle<RabbitMQ.Client.Exceptions.AlreadyClosedException>()
                .Handle<System.IO.IOException>()
                .Handle<TimeoutException>(),

            // si el circuito se abre la cola no responde  
            OnOpened = argumentos =>
            {
                registrarMensaje(
                    $"  [Circuito] ⚡ CIRCUITO ABIERTO por {configuracion.SegundosCircuitoAbierto}s. " +
                    $"RabbitMQ no responde. " +
                    $"Error: {argumentos.Outcome.Exception?.Message}");
                return ValueTask.CompletedTask;
            },

            //circuito cerrado la cola responde 
            OnClosed = argumentos =>
            {
                registrarMensaje(
                    "  [Circuito] ✓ Circuito cerrado. RabbitMQ responde correctamente.");
                return ValueTask.CompletedTask;
            },

            //circuito semiabierto permite un intento a la cola 
            OnHalfOpened = argumentos =>
            {
                registrarMensaje(
                    "  [Circuito] ~ Circuito semiabierto. Enviando intento de prueba...");
                return ValueTask.CompletedTask;
            }
        };
    }
}
