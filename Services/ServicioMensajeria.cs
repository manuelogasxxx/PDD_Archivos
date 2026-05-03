using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PDD_Archivos.Models;
using PDD_Archivos.Configuracion;
using Polly.CircuitBreaker;
using RabbitMQ.Client;
using PDD_Archivos.Modelos;



namespace PDD_Archivos.Servicios;

public class ServicioMensajeria : IDisposable
{
    private readonly ConfiguracionRabbitMq  _configuracion;
    private readonly JsonSerializerOptions  _opcionesJson;
    private readonly Polly.ResiliencePipeline _pipeline;

    // Conexión y canal se crean y se reusan en publicaciones posteriores
    private IConnection? _conexion;
    private IModel?      _canal;
    private bool         _topologiaDeclarada = false;

    public ServicioMensajeria(ConfiguracionRabbitMq configuracion, Action<string> registrar)
    {
        _configuracion = configuracion;

        _opcionesJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters           = { new JsonStringEnumConverter() }
        };

        // Construir el pipeline de resiliencia una sola vez al iniciar
        _pipeline = PoliticaResiliencia.Construir(configuracion, registrar);
    }


    // Publica el evento en RabbitMQ envuelto en el pipeline de resiliencia.
    public void Publicar(EventoArchivoEncolado evento)
    {
        try
        {
            _pipeline.Execute(() =>
            {
                AsegurarConexion();
                PublicarInterno(evento);
            });
        }
        catch (BrokenCircuitException)
        {
            // se lanza la excepcion 503 error genérico.
            throw new InvalidOperationException(
                "El sistema de procesamiento está temporalmente en mantenimiento. " +
                "Su archivo fue recibido. Por favor intente más tarde.");
        }
    }

    
    // Crea la conexión y el canal si no existen o si se cerraron por un fallo.
    private void AsegurarConexion()
    {
        if (_conexion is { IsOpen: true } && _canal is { IsOpen: true })
            return;

        var fabrica = new ConnectionFactory
        {
            Port                     = _configuracion.Puerto,
            UserName                 = _configuracion.Usuario,
            Password                 = _configuracion.Contrasena,
            VirtualHost              = _configuracion.HostVirtual,
            AutomaticRecoveryEnabled  = true,     // reconexión automática ante cortes
            NetworkRecoveryInterval   = TimeSpan.FromSeconds(5),
            RequestedHeartbeat        = TimeSpan.FromSeconds(30),
        };

        if (_configuracion.Servidores.Count == 1)
        {
            // dev-conexión simple a un solo servidor
            fabrica.HostName = _configuracion.Servidores[0];
            _conexion = fabrica.CreateConnection();
        }
        else
        {
            // caso real conecta al clúster usando la lista de servidores
            var extremos = _configuracion.Servidores
                .Select(servidor => new AmqpTcpEndpoint(servidor, _configuracion.Puerto))
                .ToList();
            _conexion = fabrica.CreateConnection(extremos);
        }

        _canal = _conexion.CreateModel();

        // Declarar Exchange, colas y bindings solo la primera vez por sesión
        if (!_topologiaDeclarada)
        {
            DeclararTopologia();
            _topologiaDeclarada = true;
        }
    }

    
    // pruebas cola simple sin replica
    
    private void DeclararTopologia()
    {
        _canal!.ExchangeDeclare(
            exchange:   _configuracion.NombreExchange,
            type:       ExchangeType.Direct,
            durable:    true,
            autoDelete: false);

        
        // Recibe mensajes que fallaron demasiadas veces en el clasificador.
        var argumentosColaFallidos = ConstruirArgumentosCola(esColaFallidos: true);
        _canal!.QueueDeclare(
            queue:      _configuracion.ColaFallidos,
            durable:    true,
            exclusive:  false,
            autoDelete: false,
            arguments:  argumentosColaFallidos);

       
        // mensajes para ser clasificados
        var argumentosColaPrincipal = ConstruirArgumentosCola(esColaFallidos: false);
        _canal.QueueDeclare(
            queue:      _configuracion.NombreCola,
            durable:    true,
            exclusive:  false,
            autoDelete: false,
            arguments:  argumentosColaPrincipal);

       
        // Solo los mensajes con esa clave exacta llegan a la cola principal
        _canal.QueueBind(
            queue:      _configuracion.NombreCola,
            exchange:   _configuracion.NombreExchange,
            routingKey: _configuracion.ClaveEnrutamiento);
    }

     
    private Dictionary<string, object> ConstruirArgumentosCola(bool esColaFallidos)
    {
        var argumentos = new Dictionary<string, object>();

        if (_configuracion.UsarColaQuorum)
        {
            
            //replicada en todos los nodos el mensaje se confirma solo cuando la mayoría de nodos lo escribió.
            argumentos["x-queue-type"] = "quorum";

            if (!esColaFallidos)
            {
                // Cola principal: después de 3 fallos el mensaje va a la cola de fallidos
                argumentos["x-delivery-limit"]          = 3;
                argumentos["x-dead-letter-exchange"]    = string.Empty;
                argumentos["x-dead-letter-routing-key"] = _configuracion.ColaFallidos;
            }
            else
            {
                // Cola de fallidos: los mensajes se guardan 7 días antes de eliminarze 
                argumentos["x-message-ttl"] = 7 * 24 * 60 * 60 * 1000;
            }
        }
        else
        {
            //Cola clásica simple, sin réplicas, perfecta para pruebas locales.
            argumentos["x-queue-type"] = "classic";

            if (!esColaFallidos)
            {
                argumentos["x-dead-letter-exchange"]    = string.Empty;
                argumentos["x-dead-letter-routing-key"] = _configuracion.ColaFallidos;
                argumentos["x-max-delivery-count"]      = 3;
            }
        }

        return argumentos;
    }

    
    // Serializa el evento a JSON y lo publica en el Exchange.
    private void PublicarInterno(EventoArchivoEncolado evento)
    {
        //el evento a JSON lo convertir a bytes
        var json  = JsonSerializer.Serialize(evento, _opcionesJson);
        var cuerpo = Encoding.UTF8.GetBytes(json);

        // Propiedades del mensaje
        var propiedades = _canal!.CreateBasicProperties();
        propiedades.Persistent      = true;               //sobrebive el eventi si la cola se cae 
        propiedades.ContentType     = "application/json";
        propiedades.ContentEncoding = "utf-8";
        propiedades.MessageId       = evento.IdArchivo;  
        propiedades.Timestamp       = new AmqpTimestamp(
            new DateTimeOffset(evento.FechaSubida).ToUnixTimeSeconds());

        // Publicar en el Exchange con la clave de enrutamiento
        _canal.BasicPublish(
            exchange:        _configuracion.NombreExchange,
            routingKey:      _configuracion.ClaveEnrutamiento,
            basicProperties: propiedades,
            body:            cuerpo);
    }

    //liberar recursos

    public void Dispose()
    {
        _canal?.Close();
        _canal?.Dispose();
        _conexion?.Close();
        _conexion?.Dispose();
    }
}
