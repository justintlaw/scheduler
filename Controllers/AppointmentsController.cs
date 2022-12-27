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

        /// <summary>
        /// This task retrieves the initial schedule of all appointments, and then loops through each request in the queue
        /// and schedules a new appointment.
        /// </summary>
        private async Task ProcessRequests()
        {
            IDictionary<int, List<DateTime>> personAppointments = new Dictionary<int, List<DateTime>>();
            IDictionary<DateTime, List<int>> appointmentDoctors = new Dictionary<DateTime, List<int>>();

            // Retrieve the initial schedule
            IEnumerable<Appointment> schedule = await GetSchedule();

            // Populate a map for persons appointments and doctors appointments
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

        /// <summary>
        /// Adds a new appointment to a dictionary with the personId as they key, and appointment date as the value.
        /// </summary>
        static private void AddPersonAppointment(int personId, DateTime appointmentTime, ref IDictionary<int, List<DateTime>> personAppointments, bool resort = true)
        {
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

        /// <summary>
        /// Adds a new appointment to a dictionary with the DateTime as they key, and doctorId as the value.
        /// </summary>
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

        /// <summary>
        /// Gets the difference in days between two dates, using only the Date (ie. not the full DateTime).
        /// </summary>
        /// <param name="dateTime1"></param>
        /// <param name="dateTime2"></param>
        /// <returns>
        /// An integer representing the number of days.
        /// </returns>
        static private int GetDaysBetweenDates(DateTime dateTime1, DateTime dateTime2)
        {
            DateOnly date1 = DateOnly.FromDateTime(dateTime1);
            DateOnly date2 = DateOnly.FromDateTime(dateTime2);

            return Math.Abs(date1.DayNumber - date2.DayNumber);
        }

        /// <summary>
        /// Determines whether a person is eligible to book an appointment given a list of appointments.
        /// </summary>
        /// <param name="appointmentsSorted"></param>
        /// <param name="date"></param>
        /// <param name="isNew"></param>
        /// <returns>
        /// A bool representing whether or not the appointment can be booked.
        /// </returns>
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

            // Find the next appointment after the date being checked
            for (int i = 0; i < appointmentsSorted.Count; i++)
            {
                if (date <= appointmentsSorted[i])
                {
                    nextDateIndex = i;
                    i = appointmentsSorted.Count;
                }
            }

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

        /// <summary>
        /// Gets the ids of doctors available for a certain DateTime.
        /// </summary>
        /// <param name="appointmentDoctors"></param>
        /// <param name="date"></param>
        /// <returns>
        /// An IEnumerable of integers representing ids of doctors available.
        /// </returns>
        static private IEnumerable<int> GetAvailableDoctorsForDate(IDictionary<DateTime, List<int>> appointmentDoctors, DateTime date)
        {
            // If the key doesn't exist yet, that means all doctors are available
            if (!appointmentDoctors.ContainsKey(date))
            {
                return DoctorIds;
            }

            return DoctorIds.Where(d1 => appointmentDoctors[date].All(d2 => d2 != d1));
        }

        /// <summary>
        /// Gets the next appointment time after the date provided in the parameters.
        /// </summary>
        /// <param name="date"></param>
        /// <param name="canBeBooked"></param>
        /// <param name="isNew"></param>
        /// <returns>
        /// A DateTime object representing the next appointment time.
        /// </returns>
        static private DateTime GetNextDate(DateTime date, bool canBeBooked, bool isNew)
        {
            // TODO: simplify this logic

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
                    if (date.Hour >= NEW_APPOINTMENT_FIRST_HOUR)
                    {
                        return date.AddHours(1);
                    }

                    // Start from 3pm for new appointments
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

        /// <summary>
        /// Generates an appointment time and doctor id of an appointment to be scheduled based on the given parameters.
        /// When an ideal date for preferred doctor and date cannot be found, the method favors the preferred doctor.
        /// </summary>
        /// <param name="appointmentsSorted"></param>
        /// <param name="appointmentDoctors"></param>
        /// <param name="preferredDates"></param>
        /// <param name="preferredDocs"></param>
        /// <param name="isNew"></param>
        /// <returns>
        /// A tuple of (DateTime, int) representing the appointment time and doctor id that can be used.
        /// </returns>
        static private (DateTime, int) GenerateAppointment(
            List<DateTime> appointmentsSorted,
            IDictionary<DateTime, List<int>> appointmentDoctors,
            List<DateTime> preferredDates,
            List<int> preferredDocs,
            bool isNew)
        {
            // First check preferred doctor/dates
            foreach (DateTime date in preferredDates)
            {
                int hour = isNew ? NEW_APPOINTMENT_FIRST_HOUR : FIRST_APPOINTMENT_HOUR;
                DateTime preferredStartDate = new(date.Year, date.Month, date.Day, hour, 0, 0);

                // Check every possible hour in the current date of preferredDates
                while (DateOnly.FromDateTime(date) == DateOnly.FromDateTime(preferredStartDate))
                {
                    IEnumerable<int> availableDoctors = GetAvailableDoctorsForDate(appointmentDoctors, preferredStartDate);
                    IEnumerable<int> availablePreferredDoctors = availableDoctors.Intersect(preferredDocs);
                    bool canBeBooked = CanBeBooked(appointmentsSorted, preferredStartDate, isNew);

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
                    IEnumerable<int> availableDoctors = GetAvailableDoctorsForDate(appointmentDoctors, currentDate);
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
                        else if (preferredDates.FindAll(d => DateOnly.FromDateTime(d) == DateOnly.FromDateTime(currentDate)).Any() || appointmentDate is null)
                        {
                            appointmentDate = currentDate;
                            appointmentDoctor = availableDoctors.First();
                        }
                    }
                }

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
            // attempting the remaining requests instead of throwing an error
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