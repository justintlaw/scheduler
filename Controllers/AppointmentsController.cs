using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Scheduler.Models;
using System.Net;
using System.Text.Json;

/*
 * Constraints:
 *  - Appointments may only be scheduled on the hour
 *  - Appointments from 8am UTC until 4pm UTC (ie. you CAN schedule an appointment for 4pm)
 *  - Weekdays from November 2021 - December 2021 only
 *  - Holidays OK
 *  - Doctor may only have one appointment per hour (but different docs same time is okay)
 *  - Each patient appointment must have at least one week of separation
 *  - New patients can only be scheduled for 3pm and 4pm
 *  
 *  NOTE: Do not need to worry about "impossible to schedule appointments"
 */
namespace Scheduler.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AppointmentsController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly ILogger<AppointmentsController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private static readonly List<int> DoctorIds = new() { 1, 2, 3 };
        private static readonly int FIRST_APPOINTMENT_HOUR = 8; // 8:00 AM
        private static readonly int LAST_APPOINTMENT_HOUR = 16; // 4:00 PM
        private static readonly int NEW_APPOINTMENT_FIRST_HOUR = 15; // 3:00 PM

        public AppointmentsController(IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<AppointmentsController> logger)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            Console.WriteLine("Token");
            Console.WriteLine(config["Client:ApiToken"]);
        }

        [HttpPost(Name = "GetAppointment")]
        public IActionResult Post()
        {
            try
            {
                ProcessRequests().GetAwaiter().GetResult();
            } catch (Exception e)
            {
                // TODO: Better error handling
                Console.WriteLine(e.ToString());
                return Problem();
            }

            return NoContent();
        }

        private async Task ProcessRequests()
        {
            //List<AppointmentRequest> appointmentRequests = new List<AppointmentRequest>();

            IEnumerable<Appointment> schedule = await GetSchedule();
            //IDictionary<int, List<DateTime>> doctorAppointments = new Dictionary<int, List<DateTime>>();
            IDictionary<int, List<DateTime>> personAppointments = new Dictionary<int, List<DateTime>>();
            IDictionary<DateTime, List<int>> appointmentDoctors = new Dictionary<DateTime, List<int>>();

            // Create a map for persons appointments and doctors appointments
            foreach (Appointment appointment in schedule)
            {
                AddAppointmentDoctor(appointment.appointmentTime, appointment.doctorId, ref appointmentDoctors);
                AddPersonAppointment(appointment.personId, appointment.appointmentTime, ref personAppointments, false);
            }

            // sort the appointments by date for each person in ascending order
            foreach (List<DateTime> appointments in personAppointments.Values)
            {
                appointments.Sort();
            }

            // Process appointment requests one at a time
            AppointmentRequest? appointmentRequest = await GetAppointmentRequest();

            while (appointmentRequest is not null)
            {
                // Retrieve the appointments for the person requesting (if any exist)
                if (!personAppointments.ContainsKey(appointmentRequest.personId))
                {
                    personAppointments.Add(appointmentRequest.personId, new List<DateTime>());
                }

                List<DateTime> appointments = personAppointments[appointmentRequest.personId];

                // Generate and schedule the most ideal appointment
                var appointmentInfo = GenerateAppointment(
                    appointments,
                    appointmentDoctors,
                    appointmentRequest.preferredDays,
                    appointmentRequest.preferredDocs,
                    appointmentRequest.isNew);
                await ScheduleAppointment(appointmentInfo.Item2, appointmentRequest.personId, appointmentRequest.requestId, appointmentInfo.Item1, appointmentRequest.isNew);

                // Update current schedule information
                AddPersonAppointment(appointmentRequest.personId, appointmentInfo.Item1, ref personAppointments);
                AddAppointmentDoctor(appointmentInfo.Item1, appointmentInfo.Item2, ref appointmentDoctors);

                Console.WriteLine("Scheduled person [{0}] with doctor [{1}] on {2}.", appointmentRequest.personId, appointmentInfo.Item2, appointmentInfo.Item1);

                appointmentRequest = await GetAppointmentRequest();
            }
        }

        static private void AddPersonAppointment(int personId, DateTime appointmentTime, ref IDictionary<int, List<DateTime>> personAppointments, bool resort = true)
        {
            // TODO: WHEN YOU ADD A NEW APPOINTMENT, YOU NEED TO RESORT THE VALUES
            if (personAppointments.TryGetValue(personId, out List<DateTime>? personSchedule))
            {
                personSchedule.Add(appointmentTime);

                if (resort)
                {
                    personSchedule.Sort();
                }
            }
            else
            {
                personAppointments.Add(personId, new List<DateTime> { appointmentTime });
            }
        }

        static private void AddAppointmentDoctor(DateTime appointmentTime, int doctorId, ref IDictionary<DateTime, List<int>> appointmentDoctors)
        {
            if (appointmentDoctors.TryGetValue(appointmentTime, out List<int>? doctorIds))
            {
                doctorIds.Add(doctorId);
            }
            else
            {
                appointmentDoctors.Add(appointmentTime, new List<int> { doctorId });
            }
        }

        // We want to use only the calendar day and ignore hours, minutes, etc.
        static private int GetDaysBetweenDates(DateTime dateTime1, DateTime dateTime2)
        {
            DateOnly date1 = DateOnly.FromDateTime(dateTime1);
            DateOnly date2 = DateOnly.FromDateTime(dateTime2);

            return Math.Abs(date1.DayNumber - date2.DayNumber);
        }

        static private bool CanBeBooked(List<DateTime> appointmentsSorted, DateTime date, bool isNew)
        {
            // The next date, as compared to the date provided in the parameters
            int? nextDateIndex = null;
            const int WAITING_PERIOD = 7;

            if (isNew)
            {
                if (date.Hour < NEW_APPOINTMENT_FIRST_HOUR || date.Hour > LAST_APPOINTMENT_HOUR)
                {
                    return false;
                }
            }

            if (appointmentsSorted.Count == 0)
            {
                return true;
            }

            for (int i = 0; i < appointmentsSorted.Count; i++)
            {
                if (date <= appointmentsSorted[i])
                {
                    nextDateIndex = i;
                    i = appointmentsSorted.Count;
                }
            }

            // NOTE: Use .Day so there is no rounding. We don't need exactly 7 days worth of time, just 7 days of separation on the calendar
            if (nextDateIndex.HasValue && nextDateIndex.GetValueOrDefault() > 0)
            {
                // The date is somewhere between the smallest and greatest values
                return GetDaysBetweenDates(appointmentsSorted[nextDateIndex.GetValueOrDefault()], date) >= WAITING_PERIOD
                    && GetDaysBetweenDates(appointmentsSorted[nextDateIndex.GetValueOrDefault() - 1], date) >= WAITING_PERIOD;
            } 
            else if (nextDateIndex.HasValue)
            {
                // The date is the smallest of all others
                return GetDaysBetweenDates(appointmentsSorted[nextDateIndex.GetValueOrDefault()], date) >= WAITING_PERIOD;
            }
            else
            {
                // The date is greater than all others
                return GetDaysBetweenDates(appointmentsSorted.Last(), date) >= WAITING_PERIOD;
            }
        }

        static private IEnumerable<int> GetAvailableDoctorsForDate(IDictionary<DateTime, List<int>> appointmentDoctors, DateTime date)
        {
            // If the key doesn't exist, that means all doctors are available
            if (!appointmentDoctors.ContainsKey(date))
            {
                return DoctorIds;
            }

            return DoctorIds.Where(d1 => appointmentDoctors[date].All(d2 => d2 != d1));
        }

        static private DateTime GetNextDate(DateTime date, bool canBeBooked, bool isNew)
        {
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            {
                // Skip the weekend
                while (date.DayOfWeek != DayOfWeek.Monday)
                {
                    date = date.AddHours(24);
                }

                return new DateTime(date.Year, date.Month, date.Day, FIRST_APPOINTMENT_HOUR, 0, 0);
            }
            else if (date.Hour >= LAST_APPOINTMENT_HOUR)
            {
                // Proceed to the next day
                date = date.AddHours(24);
                return new DateTime(date.Year, date.Month, date.Day, FIRST_APPOINTMENT_HOUR, 0, 0);
            }
            else if (!canBeBooked)
            {
                if (isNew)
                {
                    // Start from 3pm for new appointments
                    if (date.Hour >= NEW_APPOINTMENT_FIRST_HOUR)
                    {
                        return date.AddHours(1);
                    }

                    return new DateTime(date.Year, date.Month, date.Day, NEW_APPOINTMENT_FIRST_HOUR, 0, 0);
                }
                else
                {
                    // Otherwise if an appointment can't be booked proceed to the next day
                    date = date.AddHours(24);
                    return new DateTime(date.Year, date.Month, date.Day, FIRST_APPOINTMENT_HOUR, 0, 0);
                }
            }
            else
            {
                // Proceed to next hour by default
                return date.AddHours(1);
            }
        }

        static private (DateTime, int) GenerateAppointment(
            List<DateTime> appointmentsSorted,
            IDictionary<DateTime, List<int>> appointmentDoctors,
            List<DateTime> preferredDates,
            List<int> preferredDocs,
            bool isNew)
        {
            // TODO: Refactor code so there is less replication of code
            // First check preferred doctor/dates
            foreach (DateTime date in preferredDates)
            {
                int hour = isNew ? NEW_APPOINTMENT_FIRST_HOUR : FIRST_APPOINTMENT_HOUR;
                DateTime preferredStartDate = new(date.Year, date.Month, date.Day, hour, 0, 0);

                // TODO: Instead of looping through each hour, skip the day entirely, or jump to 3pm if it's new
                while (DateOnly.FromDateTime(date) == DateOnly.FromDateTime(preferredStartDate))
                {
                    // TODO: FIX ME
                    //int? preferredDoctor = appointmentDoctors[date].Intersect(preferredDocs).FirstOrDefault();

                    // ACTUAL: It checks the preferred date at 12:00am
                    // EXPECTED: It checks every valid time for the preferred date
                    IEnumerable<int> availableDoctors = GetAvailableDoctorsForDate(appointmentDoctors, preferredStartDate);

                    IEnumerable<int> availablePreferredDoctors = availableDoctors.Intersect(preferredDocs);
                    bool canBeBooked = CanBeBooked(appointmentsSorted, preferredStartDate, isNew);
                    bool preferredDoctorIsAvailable = availablePreferredDoctors.Any();

                    if (canBeBooked && availablePreferredDoctors.Any())
                    {
                        return (preferredStartDate, availablePreferredDoctors.FirstOrDefault());
                    }

                    // Proceed to the next possible time
                    preferredStartDate = GetNextDate(preferredStartDate, canBeBooked, isNew);
                }
            }

            // Starting November 1st 2021 at 8am
            DateTime startDate = new (2021, 11, 1, FIRST_APPOINTMENT_HOUR, 0, 0);
            // Ending December 31st 2021 at pm
            DateTime endDate = new (2021, 12, 31, LAST_APPOINTMENT_HOUR, 0, 0);

            DateTime currentDate = startDate; // Current date to be checked
            bool mostOptimalDateFound = false; // Whether or not the most ideal date has been found

            DateTime? appointmentDate = null; // The date to be used for the appointment
            int? appointmentDoctor = null; // The doctor to be used for the appointment

            // Loop through the available days and find the most ideal date
            // Prioritize doctor first, then preferred date
            while (!mostOptimalDateFound && currentDate <= endDate)
            {
                bool canBeBooked = CanBeBooked(appointmentsSorted, currentDate, isNew);

                if (appointmentDoctors[currentDate].Count < DoctorIds.Count)
                {
                    IEnumerable<int> availableDoctors = GetAvailableDoctorsForDate(appointmentDoctors, currentDate); //DoctorIds.Where(d1 => appointmentDoctors[currentDate].All(d2 => d2 != d1));
                    IEnumerable<int> availablePreferredDoctors = availableDoctors.Intersect(preferredDocs);

                    if (canBeBooked)
                    {
                        // Only update the preferred appointment if a more satisfactory condition is met
                        if (availablePreferredDoctors.Any())
                        {
                            appointmentDate = currentDate;
                            appointmentDoctor = availablePreferredDoctors.First();
                            mostOptimalDateFound = true;
                        }
                        // TODO: This also needs to find 
                        else if (preferredDates.FindAll(d => DateOnly.FromDateTime(d) == DateOnly.FromDateTime(currentDate)).Any() || appointmentDate is null)
                        {
                            appointmentDate = currentDate;
                            appointmentDoctor = availableDoctors.First();
                        }
                    }
                }

                //// Proceed to the next possible time
                //if (!canBeBooked)
                //{
                //    if (isNew)
                //    {
                //        // Skip to the new appointment time if it can't be booked and the appointment is new
                //        currentDate = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, NEW_APPOINTMENT_FIRST_HOUR, 0, 0);
                //    }
                //    else
                //    {
                //        // Skip the whole day if it can't be booked
                //        currentDate = currentDate.AddHours(24);
                //    }
                //}
                //else if (currentDate.Hour < LAST_APPOINTMENT_HOUR)
                //{
                //    currentDate = currentDate.AddHours(1);
                //} else
                //{
                //    currentDate = currentDate.AddHours(LAST_APPOINTMENT_HOUR); // Go from 5pm to 8am the next day

                //    // Skip the weekend
                //    if (currentDate.DayOfWeek == DayOfWeek.Saturday || currentDate.DayOfWeek == DayOfWeek.Sunday)
                //    {
                //        while (currentDate.DayOfWeek != DayOfWeek.Monday)
                //        {
                //            currentDate = currentDate.AddHours(24);
                //        }
                //    }
                //}

                currentDate = GetNextDate(currentDate, canBeBooked, isNew);
            }

            return (appointmentDate.GetValueOrDefault(), appointmentDoctor.GetValueOrDefault());
        }

        private async Task ScheduleAppointment(
            int doctorId,
            int personId,
            int requestId,
            DateTime appointmentTime,
            bool isNewPatientAppointment)
        {
            var httpRequestMessage = new HttpRequestMessage(
                 HttpMethod.Post,
                 "http://scheduling-interview-2021-265534043.us-west-2.elb.amazonaws.com/api/Scheduling/Schedule?token=" + _config["Client:ApiToken"])
            {
                Headers =
                {
                    { HeaderNames.Accept, "text/plain" },
                    { HeaderNames.UserAgent, "SchedulerApplication" }
                },
                Content = JsonContent.Create(new {
                    requestId,
                    personId,
                    doctorId,
                    appointmentTime,
                    isNewPatientAppointment
                })
            };

            var httpClient = _httpClientFactory.CreateClient();
            var httpResponseMessage = await httpClient.SendAsync(httpRequestMessage);

            // TODO: Since we are processing a batch at a time, this should probably log an error and allow the process to continue
            // instead of attempting the remaining requests instead of throwing an error
            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                // TODO: More specific error handling for API
                throw new Exception();
            }
        }

        private async Task<AppointmentRequest?> GetAppointmentRequest()
        {
            var httpRequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "http://scheduling-interview-2021-265534043.us-west-2.elb.amazonaws.com/api/Scheduling/AppointmentRequest?token=" + _config["Client:ApiToken"])
            {
                Headers =
                {
                    { HeaderNames.Accept, "text/plain" },
                    { HeaderNames.UserAgent, "SchedulerApplication" }
                }
            };

            var httpClient = _httpClientFactory.CreateClient();
            var httpResponseMessage = await httpClient.SendAsync(httpRequestMessage);

            string jsonString = await httpResponseMessage.Content.ReadAsStringAsync();

            if (httpResponseMessage.StatusCode == HttpStatusCode.OK)
            {
                // TODO: Add JSON error handling
                return JsonSerializer.Deserialize<AppointmentRequest>(jsonString);
            }
            else if (httpResponseMessage.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }
            else
            {
                // TODO: More specific error handling for API
                throw new Exception();
            }
        }

        private async Task<IEnumerable<Appointment>> GetSchedule()
        {
            var httpRequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "http://scheduling-interview-2021-265534043.us-west-2.elb.amazonaws.com/api/Scheduling/Schedule?token=" + _config["Client:ApiToken"])
            {
                Headers =
                {
                    { HeaderNames.Accept, "text/plain" },
                    { HeaderNames.UserAgent, "SchedulerApplication" }
                }
            };

            var httpClient = _httpClientFactory.CreateClient();
            var httpResponseMessage = await httpClient.SendAsync(httpRequestMessage);

            using var contentStream = await httpResponseMessage.Content.ReadAsStreamAsync();

            if (httpResponseMessage.IsSuccessStatusCode)
            {
                // TODO: Add JSON error handling
                return await JsonSerializer.DeserializeAsync<IEnumerable<Appointment>>(contentStream);
            } else
            {
                // TODO: More specific error handling for API
                throw new Exception();
            }
        }
    }
}