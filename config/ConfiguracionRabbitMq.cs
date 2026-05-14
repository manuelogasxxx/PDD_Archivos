namespace PDD_Archivos.Configuracion;


public class ConfiguracionRabbitMq
{
	//public List<string> Servidores { get; set; } = ["localhost"];
	public List<string> Servidores { get; set; } = ["172.26.160.140"];
	//Puerto AMQP
	public int Puerto { get; set; } = 5672;

	//Usuario 
	//public string Usuario { get; set; } = "guest";
	public string Usuario { get; set; } = "admin";

	//Contraseña
	//public string Contrasena { get; set; } = "guest";
	public string Contrasena { get; set; } = "admin123";

	//Virtual host / host por defecto
	public string HostVirtual { get; set; } = "/";

    
    // recibe el mensaje y lo dirige a la cola correcta según la clave de enrutamiento.
    public string NombreExchange { get; set; } = "archivos.exchange";

    
    // Cola principal donde se almacenan los mensajes de archivos pendientes de procesamiento
    public string NombreCola { get; set; } = "archivos.pendientes";

    // Enrutamiento que conecta el Exchange con la cola principal 
    public string ClaveEnrutamiento { get; set; } = "archivo.subido";

    
    // Los mensajes que fallan más de 3 veces se guarda en archivos fallidos
    public string ColaFallidos { get; set; } = "archivos.fallidos";

    
    //Tipo de cola usada
    public bool UsarColaQuorum { get; set; } = false;

    
    // Numero máximo de reintentos antes de abrir el Circuit Breaker. 
    public int MaximoReintentos { get; set; } = 5;

    //
    // Segundos de espera inicial entre reintentos se duplica en cada intento
    public int SegundosEsperaInicial { get; set; } = 2;

    
    // Después de este tiempo el circuito pasa a semiabierto y permite un intento de prueba
   
    public int SegundosCircuitoAbierto { get; set; } = 30;
}
