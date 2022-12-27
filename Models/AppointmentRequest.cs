namespace Scheduler.Models
{
    public class AppointmentRequest
    {
        public int requestId {  get; set; }
        public int personId { get; set; }
        public List<DateTime> preferredDays { get; set; } = new List<DateTime>();
        public List<int> preferredDocs { get; set; } = new List<int>();
        public bool isNew { get; set; }
    }
}
