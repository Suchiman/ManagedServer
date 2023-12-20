# Managed Server
Managed Server is a fully managed ASP.NET Core application with the sole purpose of easily allowing you to host even more ASP.NET Core applications on the same host.  
A difficulty you might encounter when trying to host multiple apps on the same host is that the default ports 80/443 can only be used by one application.  
On Windows this problem is easily solved by hosting the application in IIS.  
On anything else you need a third party reverse proxy, a way of managing the application lifetime and some boilerplate code in every application to make them play nice with the reverse proxy (forwarded for headers for example).  

Managed Server is here to tackle this problem. Built using the ASP.NET Core you know and love and using YARP, it delivers an IIS like experience on non windows platforms by managing the application lifetime and immitating the IIS integration to automagically make things like forwarded for headers work.  

Just register Managed Server as a systemd application to autostart on boot and configure your applications and you're done.  
It takes as little as adding the following to `appSettings.json`  
```json
"AspNetCoreApp": {
  "ContentRoot": "/wwwroot/app",
  "Hosts": [ "app.example.com" ],
  "Kind": "AspNetCore"
}
```
and saving the file to have ManagedServer pick up the new app immediately, start it up and route traffic arriving on the specified vhost to it.  
If you make any changes to the assemblies within `/wwwroot/app`, ManagedServer will restart the application for you after a short delay.  
