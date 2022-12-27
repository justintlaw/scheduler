namespace Scheduler.Models
{
    public class Appointment
    {
        public int doctorId { get; set; }
        public int personId { get; set; }
        public DateTime appointmentTime { get; set; }
        public bool isNewPatientAppointment { get; set; }
    }
}

/*
 * 
[
  {
    "doctorId": 1,
    "personId": 0,
    "appointmentTime": "2022-12-21T04:04:35.333Z",
    "isNewPatientAppointment": true
  }
]
 */